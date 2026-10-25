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
                error = $"无法解析：{parser.Peek()}";
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
        if (Math.Abs(rounded - Math.Round(rounded)) < 1e-12 && Math.Abs(rounded) < 1e15)
            return ((long)Math.Round(rounded)).ToString();
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
                if (c == '*' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                {
                    _pos += 2;
                    left = Math.Pow(left, ParseUnary());
                }
                else if (c == '*') { _pos++; left *= ParseUnary(); }
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
            if (_pos < _text.Length && _text[_pos] == '^')
            {
                _pos++;
                double exp = ParseUnary();
                return Math.Pow(baseVal, exp);
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
                    throw new FormatException($"无法识别的数字：{num}");
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
                throw new FormatException($"未知标识符：{name}");
            }

            throw new FormatException($"无法识别的字符：{c}");
        }

        private static double CallFunction(string name, List<double> args)
        {
            double A(int i) => i < args.Count ? args[i] : throw new FormatException($"{name} 参数不足");
            return name switch
            {
                "sqrt" => Math.Sqrt(A(0)),
                "abs" => Math.Abs(A(0)),
                "round" => args.Count > 1 ? Math.Round(A(0), (int)A(1)) : Math.Round(A(0)),
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
                _ => throw new FormatException($"未知函数：{name}"),
            };
        }
    }
}
