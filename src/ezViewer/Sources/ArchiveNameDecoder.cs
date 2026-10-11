using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EzViewer.Interop;
using SharpCompress.Common;

namespace EzViewer.Sources;

/// <summary>
/// 压缩包里没有 UTF-8 标记的文件名（zip、旧版 RAR、tar）编码不固定：中文系统打的包是 GBK，日文系统是 Shift-JIS，
/// 韩文系统是 CP949，macOS 打的包是不带标记的 UTF-8。短文件名常常同时是几种编码的合法字节序列，单独一个很难分辨，
/// 所以读目录时先把非 ASCII 字节原样留在键名里（映射成 U+DC80–U+DCFF，与 Python 的 surrogateescape 相同），
/// 读完后用整个压缩包的文件名一起判断编码，再还原成文字。
/// </summary>
internal static class ArchiveNameDecoder
{
    private const int EscapeBase = 0xDC00;
    private const int MaxSamples = 400;

    // 解码结果里每个非 ASCII 字符的得分，总分最高的编码胜出
    private const int Frequent = 3;
    private const int Common = 1;
    private const int Neutral = 0;
    private const int Unusual = -1;
    private const int Rare = -2;
    private const int Bad = -6;
    private const int SystemBonus = 1;

    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private static readonly Encoding ShiftJis = CreateEncoding(932);
    private static readonly Encoding Gbk = CreateEncoding(936);
    private static readonly Encoding Uhc = CreateEncoding(949);
    private static readonly Encoding Big5 = CreateEncoding(950);
    private static readonly int SystemCodePage = GetSystemCodePage();
    private static readonly byte[] CharFlags = BuildCharFlags();

    private const byte FlagHan = 1;
    private const byte FlagHangul = 2;

    /// <summary>SharpCompress 的 CustomDecoder：带 UTF-8 标记或纯 ASCII 的直接解码，其余先保留原始字节。</summary>
    public static string Escape(byte[] data, int index, int count, EncodingType type)
    {
        var span = new ReadOnlySpan<byte>(data, index, count);
        if (type == EncodingType.UTF8 || Ascii.IsValid(span)) return Encoding.UTF8.GetString(span);
        var chars = new char[count];
        for (int i = 0; i < count; i++)
            chars[i] = span[i] < 0x80 ? (char)span[i] : (char)(EscapeBase + span[i]);
        return new string(chars);
    }

    public static Encoding Detect(IEnumerable<string?> keys) => Detect(keys, SystemCodePage);

    internal static Encoding Detect(IEnumerable<string?> keys, int systemCodePage)
    {
        var samples = CollectSamples(keys);
        if (samples.Count == 0) return Utf8;

        var candidates = Candidates(systemCodePage);
        // 单字节代码页（西文、俄文等）系统上，合法的 UTF-8 一律按 UTF-8：UTF-8 的多字节序列按单字节解码总能凑出一串字母
        if (candidates[0].IsSingleByte && samples.TrueForAll(s => System.Text.Unicode.Utf8.IsValid(s))) return Utf8;

        Encoding best = Utf8;
        int bestScore = int.MinValue;
        foreach (var encoding in candidates)
        {
            int score = encoding.CodePage == systemCodePage ? SystemBonus : 0;
            foreach (var bytes in samples) score += Score(encoding, bytes);
            if (score > bestScore)
            {
                best = encoding;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>把 Escape 保留下来的字节按 encoding 还原成文字；不含转义字节的键名原样返回。</summary>
    public static string Resolve(string key, Encoding encoding)
    {
        if (!IsEscaped(key)) return key;
        var bytes = ToBytes(key);
        var text = encoding.GetString(bytes);
        // 个别文件名和包里其他文件编码不同（例如后来用别的工具追加的），是合法 UTF-8 就按 UTF-8
        if (text.Contains('\uFFFD') && encoding.CodePage != Utf8.CodePage && System.Text.Unicode.Utf8.IsValid(bytes))
            return Encoding.UTF8.GetString(bytes);
        return text;
    }

    internal static string DecodeName(byte[] bytes, int systemCodePage)
    {
        var key = Escape(bytes, 0, bytes.Length, EncodingType.Default);
        return Resolve(key, Detect(new[] { key }, systemCodePage));
    }

    private static bool IsEscaped(ReadOnlySpan<char> s) => s.IndexOfAnyInRange((char)(EscapeBase + 0x80), (char)(EscapeBase + 0xFF)) >= 0;

    private static byte[] ToBytes(ReadOnlySpan<char> s)
    {
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            int c = s[i];
            bytes[i] = c < 0x80 ? (byte)c : c is >= EscapeBase + 0x80 and <= EscapeBase + 0xFF ? (byte)(c - EscapeBase) : (byte)'?';
        }
        return bytes;
    }

    /// <summary>
    /// 按“/”拆开后去重：同一个目录名在包里每个文件名中都会出现，只算一次。
    /// 不按“\”拆，Shift-JIS、GBK、Big5 双字节字符的第二个字节可能就是 0x5C。
    /// </summary>
    private static List<byte[]> CollectSamples(IEnumerable<string?> keys)
    {
        var samples = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key == null || !IsEscaped(key)) continue;
            foreach (var part in key.Split('/'))
            {
                if (!IsEscaped(part) || !seen.Add(part)) continue;
                samples.Add(ToBytes(part));
                if (samples.Count >= MaxSamples) return samples;
            }
        }
        return samples;
    }

    /// <summary>系统代码页排在最前，得分相同时优先。</summary>
    private static List<Encoding> Candidates(int systemCodePage)
    {
        var list = new List<Encoding> { Utf8, Gbk, ShiftJis, Uhc, Big5 };
        int i = list.FindIndex(e => e.CodePage == systemCodePage);
        if (i > 0)
        {
            var system = list[i];
            list.RemoveAt(i);
            list.Insert(0, system);
        }
        else if (i < 0 && systemCodePage > 0)
        {
            var single = CreateEncoding(systemCodePage);
            if (single.IsSingleByte) list.Insert(0, single);
        }
        return list;
    }

    private static int Score(Encoding encoding, byte[] bytes) => encoding.CodePage switch
    {
        65001 => ScoreUtf8(bytes),
        932 => ScoreDbcs(bytes, encoding, b => b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC, t => t is >= 0x40 and <= 0xFC and not 0x7F,
            WeighShiftJisSingle, WeighShiftJis),
        936 => ScoreDbcs(bytes, encoding, b => b is >= 0x81 and <= 0xFE, t => t is >= 0x40 and <= 0xFE and not 0x7F,
            (b, _) => b == 0x80 ? Unusual : Bad, WeighGbk),
        949 => ScoreDbcs(bytes, encoding, b => b is >= 0x81 and <= 0xFE, t => t is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0x81 and <= 0xFE,
            (_, _) => Bad, WeighUhc),
        950 => ScoreDbcs(bytes, encoding, b => b is >= 0x81 and <= 0xFE, t => t is >= 0x40 and <= 0x7E or >= 0xA1 and <= 0xFE,
            (_, _) => Bad, WeighBig5),
        _ => ScoreSingleByte(bytes, encoding),
    };

    /// <param name="weighSingle">单字节字符的得分，第二个参数是前一个字节。</param>
    private static int ScoreDbcs(byte[] bytes, Encoding encoding, Func<int, bool> isLead, Func<int, bool> isTrail,
        Func<int, int, int> weighSingle, Func<int, int, char, int> weigh)
    {
        int score = 0;
        Span<byte> pair = stackalloc byte[2];
        Span<char> chars = stackalloc char[2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (b < 0x80) continue;
            if (!isLead(b) || i + 1 >= bytes.Length || !isTrail(bytes[i + 1]))
            {
                score += weighSingle(b, i > 0 ? bytes[i - 1] : 0);
                continue;
            }
            int t = bytes[++i];
            pair[0] = (byte)b;
            pair[1] = (byte)t;
            char c = encoding.GetChars(pair, chars) == 1 ? chars[0] : '\uFFFD';
            score += c == '\uFFFD' || char.GetUnicodeCategory(c) == UnicodeCategory.PrivateUse ? Bad : weigh(b, t, c);
        }
        return score;
    }

    /// <summary>半角片假名本身不加分（GBK 文字被误当成 Shift-JIS 时大多变成半角片假名），紧跟在可加浊音的假名后的ﾞﾟ才算。</summary>
    private static int WeighShiftJisSingle(int b, int prev)
    {
        if (b is < 0xA1 or > 0xDF) return Bad;
        bool voiced = b == 0xDE && prev is 0xB3 or >= 0xB6 and <= 0xC4 or >= 0xCA and <= 0xCE;
        bool semiVoiced = b == 0xDF && prev is >= 0xCA and <= 0xCE;
        return voiced || semiVoiced ? Frequent : Neutral;
    }

    private static int WeighShiftJis(int b, int t, char c)
    {
        if (IsKana(c)) return Frequent;
        if (b is 0x81 or 0x82) return Common;                             // 全角标点、数字、字母
        if (b is >= 0x88 and <= 0x9F or >= 0xE0 and <= 0xEA)              // JIS 第一、第二水准汉字
            return IsFrequentHan(c) ? Frequent : b < 0x98 || (b == 0x98 && t < 0x9F) ? Common : Neutral;
        if (b == 0x87) return Neutral;                                    // ①、Ⅰ、㈱ 等 NEC 特殊字符
        if (b <= 0x84) return Unusual;                                    // 希腊、俄文字母、制表符
        return Rare;                                                      // NEC、IBM 扩展汉字
    }

    private static int WeighGbk(int b, int t, char c)
    {
        if (t >= 0xA1)
        {
            if (b is >= 0xB0 and <= 0xF7)                                 // GB2312 一级、二级汉字
                return IsFrequentHan(c) ? Frequent : b <= 0xD7 ? Common : Neutral;
            if (b is 0xA1 or 0xA3) return Common;                         // 中文标点、全角字符
            if (b == 0xA2) return Neutral;                                // 序号
            if (b <= 0xA9) return Unusual;                                // 假名、希腊、俄文字母、拼音、制表符
        }
        return IsFrequentHan(c) ? Common : Rare;                          // GBK 扩展区：繁体字、生僻字、符号
    }

    private static int WeighUhc(int b, int t, char c)
    {
        if (c is >= '\uAC00' and <= '\uD7A3')                            // KS X 1001 常用 2350 个音节 / UHC 扩展音节
            return b is >= 0xB0 and <= 0xC8 && t >= 0xA1 ? (IsFrequentHangul(c) ? Frequent : Common) : Rare;
        if (b is >= 0xCA and <= 0xFD) return IsFrequentHan(c) ? Neutral : Unusual;   // 韩国汉字，现代文件名里很少用
        if (b is 0xA1 or 0xA3) return Common;                             // 标点、全角字符
        if (b is 0xA2 or 0xA4 or 0xA8 or 0xA9) return Neutral;            // 符号、韩文字母、圈码
        return Unusual;                                                   // 罗马数字、制表符、单位、假名、俄文字母
    }

    private static int WeighBig5(int b, int t, char c)
    {
        int code = b << 8 | t;
        if (code is >= 0xA440 and <= 0xC67E) return IsFrequentHan(c) ? Frequent : Common;   // 常用字
        if (code is >= 0xC940 and <= 0xF9D5) return IsFrequentHan(c) ? Frequent : Neutral;  // 次常用字
        if (b == 0xA1) return Common;                                     // 标点
        if (b == 0xA2) return Neutral;                                    // 符号、数字
        if (b == 0xA3) return Unusual;                                    // 注音、希腊字母
        return Rare;
    }

    private static int ScoreUtf8(byte[] bytes)
    {
        var text = Utf8.GetString(bytes);
        int score = 0;
        int prevBlock = -1;
        for (int i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int length);
            int block = -1;
            if (rune.Value >= 0x80) score += WeighUnicode(text, i, length, rune, prevBlock, out block);
            prevBlock = block;
            i += length;
        }
        return score;
    }

    /// <param name="block">字母所在的 128 字符区段，用来判断是否和前一个字母属于同一种文字；不是字母时为 -1。</param>
    private static int WeighUnicode(string text, int index, int length, Rune rune, int prevBlock, out int block)
    {
        block = -1;
        int v = rune.Value;
        if (v == 0xFFFD) return Bad;
        if (IsKana(v)) return Frequent;
        if (IsHan(v)) return IsFrequentHan(v) ? Frequent : Common;
        if (v is >= 0xAC00 and <= 0xD7A3) return IsFrequentHangul(v) ? Frequent : Common;
        if (v is >= 0x3000 and <= 0x303F or >= 0xFF00 and <= 0xFFEF) return Common;
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter:
                // 带重音的拉丁字母紧挨着 ASCII 字母、同一种文字连成串，才像真正的文字而不是乱码
                block = v >> 7;
                if (v <= 0x24F && (IsAsciiLetterAt(text, index - 1) || IsAsciiLetterAt(text, index + length))) return 3 * Common;
                return block == prevBlock ? 2 * Common : Common;
            case UnicodeCategory.Control or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned:
                return Bad;
            default:
                return Neutral;
        }
    }

    /// <summary>只有和别的字母连在一起的才算像文字：任何字节按单字节代码页解码都是合法字符，乱码里也常有零散的字母。</summary>
    private static int ScoreSingleByte(byte[] bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        int score = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80) continue;
            if (c == '\uFFFD' || char.IsControl(c)) score += Bad;
            else if (char.IsLetter(c) && (IsLetterAt(text, i - 1) || IsLetterAt(text, i + 1))) score += Common;
        }
        return score;
    }

    private static bool IsAsciiLetterAt(string s, int i) => i >= 0 && i < s.Length && char.IsAsciiLetter(s[i]);

    private static bool IsLetterAt(string s, int i) => i >= 0 && i < s.Length && char.IsLetter(s[i]);

    private static bool IsKana(int c) => c is >= 0x3041 and <= 0x3096 or >= 0x309B and <= 0x309E or >= 0x30A1 and <= 0x30FE;

    private static bool IsHan(int c) => c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F;

    private static bool IsFrequentHan(int c) => c <= 0xFFFF && (CharFlags[c] & FlagHan) != 0;

    private static bool IsFrequentHangul(int c) => c <= 0xFFFF && (CharFlags[c] & FlagHangul) != 0;

    private static Encoding CreateEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("\uFFFD"));
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    private static int GetSystemCodePage()
    {
        try
        {
            return (int)NativeMethods.GetACP();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static byte[] BuildCharFlags()
    {
        var flags = new byte[0x10000];
        foreach (var s in new[] { FrequentSimplified, FrequentTraditional, FrequentJapanese })
            foreach (char c in s) flags[c] |= FlagHan;
        foreach (char c in FrequentHangul) flags[c] |= FlagHangul;
        return flags;
    }

    // 现代汉语使用频率最高的约 1000 个字，加上漫画文件名里常见的字
    private const string FrequentSimplified =
        "的一是不了在人有我他这个们中来上大为和国地到以说时要就出会可也你对生能而子那得于着下自之年过发后作里用道行所然家种事成方多经么去法学如都同现当没动面起看定天分还进好小部其些主样理心她本前开但因只从想实日军者意无力它与长把机十民第公此已工使情明性知全三又关点正业外将两高间由问很最重并物手应战向头文体政美相见被利什二等产或新己制身果加西斯月话合回特代内信表化老给世位次度门任常先海通教儿原东声提立及比员解水名真论处走义各入几口认条平系气题活尔更别打女变四神总何电数安少报才结反受目太量再感建务做接必场件计管期市直德资命山金指克许统区保至队形社便空决治展马科司五基眼书非则听白却界达光放强即像难且权思王象完设式色路记南品住告类求据程北边死张该交规万取拉格望觉术领共确传师观清今切院让识候带导争运笑飞风步改收根干造言联持组每济车亲极林服快办议往元英士证近失转夫令准布始怎呢存未远叫台单影具罗字爱击流备兵连调深商算质团集百需价花党华城石级整府离况亚请技际约示复病息究线似官火断精满支视消越器容照须九增研写称企八功吗包片史委乎查轻易早曾除农找装广显吧阿李标谈吃图念六引历首医局突专费号尽另周较注语仅考落青随选列武红响虽推势参希古众构房半节土投某案黑维革划敌致陈律足态护七兴派孩验责营星够章音跟志底站严巴例防族供效续施留讲型料终答紧黄绝奇察母京段依批群项故按河米围江织害斗双境客纪采举杀攻父苏密低朝友诉止细愿千值仍男钱破网热助倒育属坐帝限船脸职速刻乐否刚威毛状率甚独球般普怕弹校苦创假久错承印晚兰试股拿脑预谁益阳若哪微尼继送急血惊伤素药适波夜省初喜卫源食险待述陆习置居劳财环排福纳欢雷警获模充负云停木游龙树疑层冷洲冲射略范竟句室异激汉村哈策演简卡罪判担州静退既衣您宗积余痛检差富灵协角占配征修皮挥胜降阶审沉坚善妈刘读啊超免压银买皇养伊怀执副乱抗犯追帮宣佛岁航优怪香著田铁控税左右份穿艺背阵草脚概恶块顿敢守酒岛托央户烈洋哥索胡款靠评版宝座释景顾弟登货互付伯慢欧换闻危忙核暗姐介坏讨丽良序升监临亮露永呼味野架域沙掉括舰鱼杂误湾吉减编楚肯测败屋跑梦散温困剑渐封救贵枪缺楼县尚毫移娘朋画班智亦耳恩短掌恐遗固席松秘谢鲁遇康虑幸均销钟诗藏赶剧票损忽巨炮旧端探湖录叶春乡附吸予礼港雨呀板庭妇归睛饭额含顺输摇招婚脱补谓督毒油疗旅泽材灭逐莫笔亡鲜词圣择寻厂睡博勒烟授诺伦岸奥唐卖俄炸载洛健堂旁宫喝借君禁阴园谋宋避抓荣姑孙逃牙束跳顶" +
        "卷册篇番彩页扫漫刊典贼忍刃咒谍锯猎篮珠侦柯恋魔鬼妖姬骑勇猫狐仙妹兄萌宅";

    // 上表中各字的繁体字形（由 Windows 的 LCMapStringEx 简转繁得到）
    private const string FrequentTraditional =
        "這個們來為國說時會對過發種經學現當沒動還進樣開從實軍無與長機關點業將兩間問應戰頭體見產話內給門兒東聲員論處義幾認條氣題爾別變總電數報結務場計資許統區隊決馬書則聽卻達強難權設記類據邊張該規萬覺術領確傳師觀讓識帶導爭運飛風聯組濟車親極辦議證轉準遠臺單羅愛擊備連調質團價黨華級離況亞請際約復線斷滿視須寫稱嗎輕農裝廣顯標談圖歷醫專費號盡較語僅隨選紅響雖勢參眾構節維劃敵陳態護興驗責營夠嚴續講終緊黃絕項圍織雙紀舉殺蘇訴細錢網熱屬臉職樂剛狀獨彈創錯蘭試腦預誰陽繼驚傷藥適衛險陸習勞財環納歡獲負龍樹層沖異漢簡擔靜積檢靈協揮勝階審堅媽劉讀壓銀買養懷執亂幫歲優鐵稅藝陣腳惡塊頓島戶評寶釋顧貨歐換聞壞討麗監臨艦魚雜誤灣減編測敗夢溫劍漸貴槍樓縣畫遺謝魯慮銷鐘詩趕劇損舊錄葉鄉禮婦歸飯額順輸搖脫補謂療澤滅筆鮮詞擇尋廠煙諾倫奧賣載宮陰園謀榮孫頂冊頁掃賊諜鋸獵籃偵戀騎貓" +
        "裡後";

    // 日文新字体（与简体、繁体写法都不同的常用字）
    private const string FrequentJapanese =
        "巻図絵読気様対発変楽実戦関経帰覚険剣闘弾撃転伝広数歳円済権将応総単声売続悪価芸装拡県営栄駅桜獣姫隊竜辺込払仏処拠挙継軽検験厳効雑参残歯児辞釈収従縦焼証条状乗浄畳縄譲触寝尽粋酔穂随瀬静摂専浅践銭潜繊禅双壮争捜挿巣層騒増蔵贈臓属帯滞択胆団断遅虫昼庁聴鎮鉄灯党盗稲徳独届難悩脳拝廃髪抜晩秘浜頻敏福併並弁歩宝豊毎満黙訳薬予余誉謡頼乱覧両猟緑涙類礼励戻霊齢歴恋練炉労郎楼録湾亜圧囲為医隠衛塩縁欧殴奥穏仮壊懐殻岳喝渇陥勧寛歓観祈既亀偽戯犠旧虚峡挟狭郷響暁勤謹区駆勲薫径茎恵掲渓蛍鶏欠研倹圏献顕黄鉱号黒穀砕斎剤桟蚕惨賛糸祉視湿写煮寿臭渋粛叙称渉奨剰壌嬢醸嘱慎枢斉窃節曽痩僧即堕滝嘆痴鋳徴懲勅塚逓弐覇梅麦繁蛮卑碑賓瓶侮塀餅舗褒墨翻免麺弥揺隆虜塁暦錬朗廊";

    // 韩文里最常用的约 400 个音节（KS X 1001 收录的 2350 个常用音节之内）
    private const string FrequentHangul =
        "가각간갈감강개거건걸검것게겠격견결경계고곡곤골공과관광교구국군권귀그극근글금기긴길김까께꼬꾸끝" +
        "나날남내너넣네녀년노놀누눈는늘니님다단달담답당대더던데도독돌동되된될두둘뒤드든들등디따때떤또뜻" +
        "라락란람랑래러럼렇레려력련렬령로록론료루류르를름리린림립마막만많말망매머먹먼메며면명몇모목몰몸못무문물뭐미민밀" +
        "바박반받발밤방배백버번벌범법벽변별병보복본볼봉부북분불브비빛사산살삼상새색생서석선설섬성세센셨소속손송수숙순술쉬스슬습승시식신실심십싶쓰씨" +
        "아악안않알았앞애야약양어억언얼엄업없었에엔여역연열염영예오온올옷와완왕외요욕용우운울움웃원월위유육윤으은을음응의이익인일임입있" +
        "자작잔잘잠장재저적전절점정제져조족존종좋죠주죽준줄중즈즐증지직진질집짓짜째쪽차착찰참창찾채책처천철첫청체초촌총최추축출충취측치친칠침" +
        "카칼캐커컬컴케코콘쿠크큰클키타탄탈탐탑태택터테텔토통투툰트특티팀파판팔패퍼페편평포폭표푸품풍프플피필" +
        "하학한할함합항해했행향허험헤혀현혈형호혹혼홍화확환활황회획효후훈휘휴흐흑희히힘";
}
