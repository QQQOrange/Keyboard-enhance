namespace CapsLockPlus.Core.Text;

/// <summary>
/// Caps+Tab 的表达式求值器（对应上游用 IE 引擎跑 JS 的做法）。
/// 这里改用自带的表达式解析器：无依赖、无 IE、Win11 下也能用。
/// 支持 + - * / % ^、括号、小数、科学计数法、常见常量（pi、e）、
/// 以及 sqrt/abs/round/floor/ceil/sin/cos/tan/log/ln/min/max/pow 等函数。
/// </summary>
public static class ExpressionEvaluator
{
    public static bool TryEvaluate(string expression, out double value, out string error)
    {
        value = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "空表达式";
            return false;
        }
        try
        {
            var parser = new Parser(Normalize(expression));
            value = parser.ParseExpression();
            parser.SkipSpaces();
            if (!parser.AtEnd)
            {
                error = Loc.F("无法解析：{0}", parser.Peek());
                return false;
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "结果不是有限数";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把结果格式化成好看的样子（0.1+0.2 得到 0.3 而不是 0.30000000000000004）。</summary>
    public static string Format(double value)
    {
        double rounded = Math.Round(value, 12);

        /* 整数走 long。
         *
         * 阈值必须是 **long 的精确范围**（约 9.2e18），不能是 1e15：
         * 超过 1e15 的整数会掉到 "0.############" 那条分支，而那个格式最多
         * 15 位有效数字 —— `2^50` 会被打平成 1125899906842620（差 4），
         * 而 Caps+Tab 会把这个错值直接写回文档。
         * double 在 2^53 以上本来就只能是整数，所以"整数值就按 long 输出"
         * 对任何可表示的值都成立。 */
        if (rounded == Math.Floor(rounded) && Math.Abs(rounded) < 9.2e18)
            return ((long)rounded).ToString(System.Globalization.CultureInfo.InvariantCulture);

        return rounded.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '×' => '*',
                '÷' => '/',
                '－' => '-',
                '＋' => '+',
                '（' => '(',
                '）' => ')',
                '，' => ',',
                '。' => '.',
                '％' => '%',
                '^' => '^',
                _ => c,
            });
        }
        return sb.ToString();
    }

    // 不用 C# 12 主构造函数：net7.0-windows 线的 LangVersion 锁在 C# 11，
    // 主构造在那儿编译不过。传统字段 + 构造对两条线完全等价。
    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text) { _text = text; _pos = 0; }

        public bool AtEnd => _pos >= _text.Length;
        public char Peek() => _text[_pos];
        public void SkipSpaces() { while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++; }

        private bool Match(char c)
        {
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == c) { _pos++; return true; }
            return false;
        }

        public double ParseExpression()
        {
            double left = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Match('+')) left += ParseTerm();
                else if (Match('-')) left -= ParseTerm();
                else return left;
            }
        }

        private double ParseTerm()
        {
            double left = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (_pos >= _text.Length) return left;
                char c = _text[_pos];
                /* 注意：`**` 不在这里处理 —— 它和 `^` 一样是幂运算，
                 * 必须走 ParsePower（右结合、只作用于紧邻的操作数）。
                 * 放在这里会让 `2*3**2` 变成 (2*3)^2 = 36，而 `2*3^2` 是 18，
                 * 同一道式子两种写法给出两个答案。 */
                if (c == '*') { _pos++; left *= ParseUnary(); }
                else if (c == '/')
                {
                    _pos++;
                    double d = ParseUnary();
                    left = d == 0 ? throw new DivideByZeroException("除数不能为 0") : left / d;
                }
                else if (c == '%')
                {
                    _pos++;
                    double d = ParseUnary();
                    left = d == 0 ? throw new DivideByZeroException("取模的除数不能为 0") : left % d;
                }
                else return left;
            }
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            return ParsePower();
        }

        private double ParsePower()
        {
            double baseVal = ParsePrimary();
            SkipSpaces();
            if (_pos >= _text.Length) return baseVal;

            // `^` 和 `**` 是同义词，都按右结合处理（2^3^2 = 2^(3^2) = 512）
            if (_text[_pos] == '^')
            {
                _pos++;
                return Math.Pow(baseVal, ParseUnary());
            }
            if (_text[_pos] == '*' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
            {
                _pos += 2;
                return Math.Pow(baseVal, ParseUnary());
            }
            return baseVal;
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (_pos >= _text.Length) throw new FormatException("表达式不完整");
            char c = _text[_pos];

            if (c == '(')
            {
                _pos++;
                double v = ParseExpression();
                if (!Match(')')) throw new FormatException("缺少右括号");
                return v;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = _pos;
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
                // 科学计数法
                if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    int save = _pos;
                    _pos++;
                    if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                    if (_pos < _text.Length && char.IsDigit(_text[_pos]))
                    {
                        while (_pos < _text.Length && char.IsDigit(_text[_pos])) _pos++;
                    }
                    else _pos = save;
                }
                var num = _text[start.._pos];
                if (!double.TryParse(num, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    throw new FormatException(Loc.F("无法识别的数字：{0}", num));
                return v;
            }

            if (char.IsLetter(c))
            {
                int start = _pos;
                while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                string name = _text[start.._pos].ToLowerInvariant();
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                }
                if (Match('('))
                {
                    var args = new List<double>();
                    if (!Match(')'))
                    {
                        do { args.Add(ParseExpression()); } while (Match(','));
                        if (!Match(')')) throw new FormatException("函数缺少右括号");
                    }
                    return CallFunction(name, args);
                }
                throw new FormatException(Loc.F("未知标识符：{0}", name));
            }

            throw new FormatException(Loc.F("无法识别的字符：{0}", c));
        }

        private static double CallFunction(string name, List<double> args)
        {
            double A(int i) => i < args.Count ? args[i] : throw new FormatException(Loc.F("{0} 参数不足", name));
            return name switch
            {
                "sqrt" => Math.Sqrt(A(0)),
                "abs" => Math.Abs(A(0)),
                /* round() 用"四舍五入"而不是 .NET 默认的银行家舍入：
                 * 计算器里 round(2.5) 应该是 3，不是 2；
                 * 亮度/音量步进那边（BrightnessAccel）也是特意避开这个坑的。 */
                "round" => args.Count > 1
                    ? Math.Round(A(0), (int)A(1), MidpointRounding.AwayFromZero)
                    : Math.Round(A(0), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(A(0)),
                "ceil" => Math.Ceiling(A(0)),
                "sin" => Math.Sin(A(0)),
                "cos" => Math.Cos(A(0)),
                "tan" => Math.Tan(A(0)),
                "asin" => Math.Asin(A(0)),
                "acos" => Math.Acos(A(0)),
                "atan" => Math.Atan(A(0)),
                "log" => Math.Log10(A(0)),
                "ln" => Math.Log(A(0)),
                "exp" => Math.Exp(A(0)),
                "pow" => Math.Pow(A(0), A(1)),
                "min" => args.Min(),
                "max" => args.Max(),
                "sum" => args.Sum(),
                "avg" => args.Average(),
                "deg" => A(0) * 180 / Math.PI,
                "rad" => A(0) * Math.PI / 180,
                _ => throw new FormatException(Loc.F("未知函数：{0}", name)),
            };
        }
    }
}
