using System.Globalization;
using System.Text;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.Core.Macro;

/// <summary>
/// 宏的变量表：让宏能"记住一个值"，并在后面的步骤里用它。
///
/// ## 为什么宏需要变量
///
/// 没有变量时，宏能表达的只有"做这几步" —— 一旦要"重复 10 次、每次打上序号"，
/// 就只能配 10 条手写步骤。有了变量，循环次数本身就能当值用（`{i}`），
/// 这是宏从"按键回放"变成"小程序"的分界线。
///
/// ## 为什么占位符而不是表达式语言
///
/// 只做 `{名字}` 文本替换 + 六种比较运算符，不引入算术、函数、嵌套。
/// 完整的脚本语言听起来更强，但代价是：用户写错一个字符，宏会**静默**走错分支，
/// 而"文本替换"错了一眼就能看出来 —— 展开后打出来的就是错的。
///
/// ## 一条硬规则：未知占位符原样保留
///
/// `{没有这个变量}` 不会被替换成空串，而是原样留着。
/// 换成空串的话，用户拼错变量名时文本会**悄悄少一段**，比看到 `{nmae}` 更难发现。
/// </summary>
public sealed class MacroVars
{
    /// <summary>变量名的最大长度（防止把一整段文本当名字塞进来）。</summary>
    public const int MaxNameLength = 24;

    /// <summary>单个值的最大长度：够放 URL 和一行文本，但挡住"把整个剪贴板塞进去"。</summary>
    public const int MaxValueLength = 4096;

    /// <summary>循环计数的默认变量名。循环步骤留空时用它，让 `{i}` 开箱可用。</summary>
    public const string DefaultLoopVar = "i";

    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前变量个数（自检与界面提示用）。</summary>
    public int Count => _map.Count;

    /// <summary>清空（每轮播放开始时调用，避免上一轮的残留值影响这一轮）。</summary>
    public void Clear() => _map.Clear();

    /// <summary>
    /// 变量名是否合法：非空、不超过 <see cref="MaxNameLength"/>、不含空白与花括号。
    ///
    /// 不允许花括号是因为它同时是占位符的分隔符 ——
    /// 允许 `a{b` 这种名字会让展开规则变得无法解释。
    /// </summary>
    public static bool IsValidName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0 || n.Length > MaxNameLength) return false;

        foreach (char c in n)
        {
            if (char.IsWhiteSpace(c) || c == '{' || c == '}') return false;
        }
        return true;
    }

    /// <summary>写入一个变量。名字非法或值为空时忽略（返回 false）。</summary>
    public bool TrySet(string? name, string? value)
    {
        if (IsValidName(name) == false) return false;

        string v = value ?? "";
        if (v.Length > MaxValueLength) v = v[..MaxValueLength];

        _map[name!.Trim()] = v;
        return true;
    }

    /// <summary>写入（名字非法时静默忽略，播放流程不因此中断）。</summary>
    public void Set(string? name, string? value) => TrySet(name, value);

    /// <summary>读一个变量；没有则空串。</summary>
    public string Get(string? name)
        => IsValidName(name) && _map.TryGetValue(name!.Trim(), out string? v) ? v : "";

    /// <summary>变量是否已定义（注意：定义为空串也算已定义）。</summary>
    public bool Has(string? name) => IsValidName(name) && _map.ContainsKey(name!.Trim());

    /// <summary>
    /// 展开文本里的 `{变量}` 占位符。
    ///
    /// 只展开一层：值里即使含 `{x}` 也不会再展开，
    /// 否则"变量引用自己"会无限递归 —— 播放线程上崩掉等于宏卡死。
    /// </summary>
    public string Expand(string? template)
    {
        if (string.IsNullOrEmpty(template)) return "";
        if (template.IndexOf('{') < 0) return template;      // 绝大多数步骤走这条快路

        var sb = new StringBuilder(template.Length + 16);
        int i = 0;

        while (i < template.Length)
        {
            char c = template[i];
            if (c != '{') { sb.Append(c); i++; continue; }

            int close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                // 没有右括号：后面全是普通文本
                sb.Append(template, i, template.Length - i);
                break;
            }

            string name = template.Substring(i + 1, close - i - 1).Trim();

            if (IsValidName(name) && _map.TryGetValue(name, out string? v))
            {
                sb.Append(v);
                i = close + 1;
            }
            else
            {
                /* 未知占位符：原样保留 `{`，从下一个字符继续扫。
                 * 这样 `{a}{unknown}{b}` 里的 {a} 和 {b} 仍然会被展开。 */
                sb.Append('{');
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 求值一条变量比较式，形如 `i &gt;= 3`、`name = 张三`、`done`（只判断非空）。
    ///
    /// 返回 false 表示**表达式本身没法解释**（空串），由调用方决定怎么处理；
    /// 表达式能解释时 <paramref name="result"/> 是判定结果。
    /// </summary>
    public bool TryCompare(string? expr, out bool result)
    {
        result = false;

        string e = (expr ?? "").Trim();
        if (e.Length == 0) return false;

        /* 运算符按"长的优先"匹配顺序排列：
         * 否则 `>=` 会先被 `>` 认走，右边多出一个 `=`，比较值就错了。 */
        string[] ops = { ">=", "<=", "!=", "==", "=", ">", "<" };

        foreach (string op in ops)
        {
            int at = e.IndexOf(op, StringComparison.Ordinal);
            if (at <= 0) continue;                 // 左边必须有变量名

            string name = e[..at].Trim();
            string right = Expand(e[(at + op.Length)..].Trim());
            if (name.Length == 0) continue;

            string left = Get(name);
            result = Compare(left, right, op);
            return true;
        }

        // 没有运算符：`done` 表示"这个变量有值"
        result = !string.IsNullOrEmpty(Get(e));
        return true;
    }

    private static bool Compare(string left, string right, string op)    {
        if (op is "=" or "==")
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        if (op == "!=")
            return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        /* 两边都是整数就按数值比（否则 "10" < "9" 会成立，与直觉相反）；
         * 有一边不是数字就退回字符串序 —— 宏里的变量大多是文本，报错不如给个确定行为。 */
        bool okA = long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out long a);
        bool okB = long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b);
        bool numeric = okA && okB;

        int cmp = numeric
            ? a.CompareTo(b)
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

        return op switch
        {
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            _ => false,
        };
    }

    /// <summary>
    /// 静态检查一个宏里的变量用法（**不执行**，只读步骤表）。
    ///
    /// ## 为什么需要它
    ///
    /// 变量的失败方式是**静默**的：引用了不存在的变量，宏不会报错，
    /// 只会把 `{nmae}` 原样打出去 —— 用户看到一串花括号，不知道是宏配错了还是软件坏了。
    /// 播放完再猜成本很高，不如在编辑时就指出来。
    ///
    /// ## 为什么不做顺序分析
    ///
    /// 只检查"宏里有没有定义过这个变量"，不检查"是否在本步之前定义"。
    /// 精确的顺序分析要模拟 if 分支与循环，误报会把正当配置说成错误 ——
    /// 而"拼错变量名"这个最常见的场景，位置无关的检查已经能全部覆盖。
    /// </summary>
    public static List<string> Audit(IReadOnlyList<Core.Settings.MacroStep>? steps)
    {
        var problems = new List<string>();
        if (steps == null) return problems;

        // 先收集所有"可能被定义"的变量名（含默认循环计数 i）
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultLoopVar };
        foreach (var s in steps)
        {
            if (s == null) continue;
            if (s.Kind is "setVar" or "loop" && !string.IsNullOrWhiteSpace(s.VarName))
                defined.Add(s.VarName.Trim());
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s == null) continue;
            int no = i + 1;

            // ---- 定义侧：名字本身写错 ----
            if (s.Kind is "setVar" or "loop")
            {
                string name = (s.VarName ?? "").Trim();

                if (s.Kind == "setVar" && name.Length == 0)
                {
                    problems.Add(Loc.F(
                        "第 {0} 步的「设置变量」还没填变量名，这一步不会生效。", no));
                }
                else if (name.Length > 0 && !IsValidName(name))
                {
                    problems.Add(Loc.F(
                        "第 {0} 步的变量名「{1}」不能用（不能含空格或花括号）。", no, name));
                }
            }

            // ---- 引用侧：用了没定义过的名字 ----
            foreach (string text in ReferencedTexts(s))
            {
                foreach (string name in Placeholders(text))
                {
                    if (IsValidName(name) && !defined.Contains(name))
                    {
                        problems.Add(Loc.F(
                            "第 {0} 步引用了变量「{1}」，但宏里没有任何地方定义它 —— 会原样打出花括号。",
                            no, name));
                    }
                }
            }
        }

        return problems;
    }

    /// <summary>这一步里所有会被展开的文本（用于引用检查）。</summary>
    private static IEnumerable<string> ReferencedTexts(Core.Settings.MacroStep s)
    {
        switch (s.Kind)
        {
            case "text":
                if (!string.IsNullOrEmpty(s.Text)) yield return s.Text;
                break;

            case "setVar":
                if (!string.IsNullOrEmpty(s.VarValue)) yield return s.VarValue;
                break;

            case "action":
                if (!string.IsNullOrEmpty(s.ActionArg)) yield return s.ActionArg;
                break;

            case "if":
            case "waitFor":
                // 变量条件形如 `i >= 3`：左边是变量名，右边是值
                if (string.Equals(s.Cond, MacroCondition.Var, StringComparison.OrdinalIgnoreCase))
                {
                    string expr = (s.CondValue ?? "").Trim();
                    foreach (string op in new[] { ">=", "<=", "!=", "==", "=", ">", "<" })
                    {
                        int at = expr.IndexOf(op, StringComparison.Ordinal);
                        if (at <= 0) continue;
                        yield return "{" + expr[..at].Trim() + "}";
                        yield return expr[(at + op.Length)..];
                        break;
                    }
                    if (expr.Length > 0 && expr.IndexOfAny(new[] { '>', '<', '=', '!' }) < 0)
                        yield return "{" + expr + "}";
                }
                break;
        }
    }

    /// <summary>取出文本里的 `{名字}`（未闭合的左括号忽略）。</summary>
    private static IEnumerable<string> Placeholders(string? text)
    {
        string t = text ?? "";
        int i = 0;
        while (i < t.Length)
        {
            int open = t.IndexOf('{', i);
            if (open < 0) yield break;

            int close = t.IndexOf('}', open + 1);
            if (close < 0) yield break;

            yield return t.Substring(open + 1, close - open - 1).Trim();
            i = close + 1;
        }
    }
}
