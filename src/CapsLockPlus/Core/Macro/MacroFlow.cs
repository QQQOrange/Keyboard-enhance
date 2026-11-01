using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Macro;

/// <summary>
/// 宏里流程控制标记（if / else / endIf / loop / endLoop）的配对关系。
///
/// ## 为什么用"成对标记"而不是嵌套结构
///
/// 宏在设置里是一条平铺的步骤列表，界面也是"上移/下移/插入/删除"这一套。
/// 如果把数据改成树，UI 全部要重写、老配置要迁移、JSON 结构也会变。
/// 平铺 + 配对标记的代价只有一处：**播放前要算一次表**，
/// 换来的是老宏原样能播、界面交互不变。
///
/// ## 配对表的用途
///
/// 播放时用程序计数器（pc）跳转：
///   · 条件不成立 → 跳到 else 之后（没有 else 就跳到 endIf 之后）
///   · then 分支走完遇到 else → 跳到 endIf 之后
///   · endLoop 且还有次数 → 跳回 loop 之后
///
/// 这张表在播放开始时算一次，播放过程中只查字典 —— 不在热路径上做字符串比较。
/// </summary>
public sealed class MacroFlow
{
    private readonly Dictionary<int, int> _ifEnd = new();     // if → endIf
    private readonly Dictionary<int, int> _ifElse = new();    // if → else（没有则为 -1）
    private readonly Dictionary<int, int> _elseEnd = new();   // else → endIf
    private readonly Dictionary<int, int> _elseIf = new();    // else → 它所属的 if
    private readonly Dictionary<int, int> _loopEnd = new();   // loop → endLoop
    private readonly Dictionary<int, int> _endLoopOf = new(); // endLoop → loop

    /// <summary>配对是否完整（false 时播放会降级为"忽略控制标记、按顺序执行"）。</summary>
    public bool Ok { get; private init; } = true;

    /// <summary>配对失败的原因（用户可读，含第几步）。</summary>
    public string? Error { get; private init; }

    /// <summary>这个宏里有没有流程控制标记。没有的话播放走原来的直线路径。</summary>
    public bool HasFlow { get; private set; }

    /// <summary>
    /// 扫描一遍步骤，算出配对关系。
    /// 不抛异常：配对错误通过 <see cref="Ok"/> / <see cref="Error"/> 返回，
    /// 由播放器决定"报错并停止"还是"忽略标记继续"。
    /// </summary>
    public static MacroFlow Compile(IReadOnlyList<MacroStep> steps)
    {
        var flow = new MacroFlow();
        var stack = new Stack<(string Kind, int Index)>();

        for (int i = 0; i < steps.Count; i++)
        {
            string kind = steps[i].Kind;

            switch (kind)
            {
                case "if":
                    if (stack.Count > 0 && stack.Peek().Kind == "else")
                        return Fail(Loc.F("第 {0} 步的「如果」放在了「否则」里面", i + 1));
                    stack.Push(("if", i));
                    flow.HasFlow = true;
                    break;

                case "else":
                {
                    if (stack.Count == 0 || stack.Peek().Kind != "if")
                        return Fail(Loc.F("第 {0} 步的「否则」没有对应的「如果」", i + 1));

                    var (_, ifIndex) = stack.Pop();
                    flow._ifElse[ifIndex] = i;
                    flow._elseIf[i] = ifIndex;          // 记住归属：endIf 时还要用它回填
                    stack.Push(("else", i));
                    flow.HasFlow = true;
                    break;
                }

                case "endIf":
                {
                    if (stack.Count == 0)
                        return Fail(Loc.F("第 {0} 步的「结束如果」没有对应的「如果」", i + 1));

                    var (kind2, openIndex) = stack.Pop();

                    if (kind2 == "if")
                    {
                        flow._ifElse.TryAdd(openIndex, -1);
                        flow._ifEnd[openIndex] = i;
                    }
                    else if (kind2 == "else")
                    {
                        /* else 在入栈时已经把「if」弹掉了（否则没法判断"else 后面又来一个 else"），
                         * 所以这里靠 _elseIf 回填，不能再往栈里找 —— 这正是第一版写错的地方：
                         * 嵌套 if/else 会一直报"找不到配对的如果"。 */
                        flow._elseEnd[openIndex] = i;

                        if (!flow._elseIf.TryGetValue(openIndex, out int ifIndex))
                            return Fail(Loc.F("第 {0} 步的「结束如果」找不到配对的「如果」", i + 1));

                        flow._ifEnd[ifIndex] = i;
                    }
                    flow.HasFlow = true;
                    break;
                }

                case "loop":
                    stack.Push(("loop", i));
                    flow.HasFlow = true;
                    break;

                case "endLoop":
                {
                    if (stack.Count == 0 || stack.Peek().Kind != "loop")
                        return Fail(Loc.F("第 {0} 步的「结束循环」没有对应的「循环」", i + 1));

                    var (_, loopIndex) = stack.Pop();
                    flow._loopEnd[loopIndex] = i;
                    flow._endLoopOf[i] = loopIndex;
                    flow.HasFlow = true;
                    break;
                }
            }
        }

        if (stack.Count > 0)
        {
            var (kind, index) = stack.Peek();
            string name = kind switch
            {
                "if" => Loc.T("如果"),
                "else" => Loc.T("否则"),
                "loop" => Loc.T("循环"),
                _ => kind,
            };
            return Fail(Loc.F("第 {0} 步的「{1}」没有闭合", index + 1, name));
        }

        return flow;

        static MacroFlow Fail(string message) => new() { Ok = false, Error = message, HasFlow = true };
    }

    /// <summary>if 对应的 else 下标（-1 = 没有 else）。</summary>
    public int ElseOf(int ifIndex) => _ifElse.TryGetValue(ifIndex, out int v) ? v : -1;

    /// <summary>if 对应的 endIf 下标（-1 = 配对失败）。</summary>
    public int EndOfIf(int ifIndex) => _ifEnd.TryGetValue(ifIndex, out int v) ? v : -1;

    /// <summary>endLoop 对应的 loop 下标（-1 = 配对失败）。</summary>
    public int LoopOf(int endLoopIndex) => _endLoopOf.TryGetValue(endLoopIndex, out int v) ? v : -1;

    /// <summary>
    /// 条件不成立时该跳到哪。
    /// 有 else 跳到 else 的下一条（执行 else 分支）；
    /// 没有 else 跳到 endIf 的下一条。
    /// </summary>
    public int SkipTo(int ifIndex)
    {
        int e = ElseOf(ifIndex);
        if (e >= 0) return e + 1;

        int end = EndOfIf(ifIndex);
        return end >= 0 ? end + 1 : ifIndex + 1;
    }

    /// <summary>then 分支走完（撞到 else）时该跳到哪：endIf 的下一条。</summary>
    public int AfterElse(int elseIndex)
        => _elseEnd.TryGetValue(elseIndex, out int end) && end >= 0 ? end + 1 : elseIndex + 1;

    /// <summary>循环体执行完后该不该再跳回开头。</summary>
    public bool ShouldRepeat(int loopIndex, int done, int total) => total <= 0 || done < total;
}
