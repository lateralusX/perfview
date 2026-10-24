using System.Runtime.CompilerServices;
using System.Threading.Tasks;
namespace WpfAsyncScenarios;
internal static partial class Runner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level01Async(RunState state)
    {
        state.Enter(1);
        WorkResult result = await Depth08Level02Async(state);
        state.AfterAwaitAndWork(1, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level02Async(RunState state)
    {
        state.Enter(2);
        WorkResult result = await Depth08Level03Async(state);
        state.AfterAwaitAndWork(2, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level03Async(RunState state)
    {
        state.Enter(3);
        WorkResult result = await Depth08Level04Async(state);
        state.AfterAwaitAndWork(3, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level04Async(RunState state)
    {
        state.Enter(4);
        WorkResult result = await Depth08Level05Async(state);
        state.AfterAwaitAndWork(4, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level05Async(RunState state)
    {
        state.Enter(5);
        WorkResult result = await Depth08Level06Async(state);
        state.AfterAwaitAndWork(5, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level06Async(RunState state)
    {
        state.Enter(6);
        WorkResult result = await Depth08Level07Async(state);
        state.AfterAwaitAndWork(6, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level07Async(RunState state)
    {
        state.Enter(7);
        WorkResult result = await Depth08Level08Async(state);
        state.AfterAwaitAndWork(7, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth08Level08Async(RunState state)
    {
        state.Enter(8);
        state.SuspendedRound = 0;
        await state.Gates[0].Task;
        state.AfterAwaitAndWork(8, 0);
        state.SuspendedRound = 1;
        await state.Gates[1].Task;
        return state.AfterAwaitAndWork(8, 1);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level01Async(RunState state)
    {
        state.Enter(1);
        WorkResult result = await Depth32Level02Async(state);
        state.AfterAwaitAndWork(1, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level02Async(RunState state)
    {
        state.Enter(2);
        WorkResult result = await Depth32Level03Async(state);
        state.AfterAwaitAndWork(2, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level03Async(RunState state)
    {
        state.Enter(3);
        WorkResult result = await Depth32Level04Async(state);
        state.AfterAwaitAndWork(3, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level04Async(RunState state)
    {
        state.Enter(4);
        WorkResult result = await Depth32Level05Async(state);
        state.AfterAwaitAndWork(4, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level05Async(RunState state)
    {
        state.Enter(5);
        WorkResult result = await Depth32Level06Async(state);
        state.AfterAwaitAndWork(5, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level06Async(RunState state)
    {
        state.Enter(6);
        WorkResult result = await Depth32Level07Async(state);
        state.AfterAwaitAndWork(6, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level07Async(RunState state)
    {
        state.Enter(7);
        WorkResult result = await Depth32Level08Async(state);
        state.AfterAwaitAndWork(7, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level08Async(RunState state)
    {
        state.Enter(8);
        WorkResult result = await Depth32Level09Async(state);
        state.AfterAwaitAndWork(8, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level09Async(RunState state)
    {
        state.Enter(9);
        WorkResult result = await Depth32Level10Async(state);
        state.AfterAwaitAndWork(9, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level10Async(RunState state)
    {
        state.Enter(10);
        WorkResult result = await Depth32Level11Async(state);
        state.AfterAwaitAndWork(10, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level11Async(RunState state)
    {
        state.Enter(11);
        WorkResult result = await Depth32Level12Async(state);
        state.AfterAwaitAndWork(11, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level12Async(RunState state)
    {
        state.Enter(12);
        WorkResult result = await Depth32Level13Async(state);
        state.AfterAwaitAndWork(12, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level13Async(RunState state)
    {
        state.Enter(13);
        WorkResult result = await Depth32Level14Async(state);
        state.AfterAwaitAndWork(13, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level14Async(RunState state)
    {
        state.Enter(14);
        WorkResult result = await Depth32Level15Async(state);
        state.AfterAwaitAndWork(14, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level15Async(RunState state)
    {
        state.Enter(15);
        WorkResult result = await Depth32Level16Async(state);
        state.AfterAwaitAndWork(15, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level16Async(RunState state)
    {
        state.Enter(16);
        WorkResult result = await Depth32Level17Async(state);
        state.AfterAwaitAndWork(16, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level17Async(RunState state)
    {
        state.Enter(17);
        WorkResult result = await Depth32Level18Async(state);
        state.AfterAwaitAndWork(17, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level18Async(RunState state)
    {
        state.Enter(18);
        WorkResult result = await Depth32Level19Async(state);
        state.AfterAwaitAndWork(18, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level19Async(RunState state)
    {
        state.Enter(19);
        WorkResult result = await Depth32Level20Async(state);
        state.AfterAwaitAndWork(19, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level20Async(RunState state)
    {
        state.Enter(20);
        WorkResult result = await Depth32Level21Async(state);
        state.AfterAwaitAndWork(20, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level21Async(RunState state)
    {
        state.Enter(21);
        WorkResult result = await Depth32Level22Async(state);
        state.AfterAwaitAndWork(21, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level22Async(RunState state)
    {
        state.Enter(22);
        WorkResult result = await Depth32Level23Async(state);
        state.AfterAwaitAndWork(22, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level23Async(RunState state)
    {
        state.Enter(23);
        WorkResult result = await Depth32Level24Async(state);
        state.AfterAwaitAndWork(23, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level24Async(RunState state)
    {
        state.Enter(24);
        WorkResult result = await Depth32Level25Async(state);
        state.AfterAwaitAndWork(24, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level25Async(RunState state)
    {
        state.Enter(25);
        WorkResult result = await Depth32Level26Async(state);
        state.AfterAwaitAndWork(25, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level26Async(RunState state)
    {
        state.Enter(26);
        WorkResult result = await Depth32Level27Async(state);
        state.AfterAwaitAndWork(26, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level27Async(RunState state)
    {
        state.Enter(27);
        WorkResult result = await Depth32Level28Async(state);
        state.AfterAwaitAndWork(27, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level28Async(RunState state)
    {
        state.Enter(28);
        WorkResult result = await Depth32Level29Async(state);
        state.AfterAwaitAndWork(28, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level29Async(RunState state)
    {
        state.Enter(29);
        WorkResult result = await Depth32Level30Async(state);
        state.AfterAwaitAndWork(29, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level30Async(RunState state)
    {
        state.Enter(30);
        WorkResult result = await Depth32Level31Async(state);
        state.AfterAwaitAndWork(30, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level31Async(RunState state)
    {
        state.Enter(31);
        WorkResult result = await Depth32Level32Async(state);
        state.AfterAwaitAndWork(31, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth32Level32Async(RunState state)
    {
        state.Enter(32);
        state.SuspendedRound = 0;
        await state.Gates[0].Task;
        state.AfterAwaitAndWork(32, 0);
        state.SuspendedRound = 1;
        await state.Gates[1].Task;
        return state.AfterAwaitAndWork(32, 1);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level01Async(RunState state)
    {
        state.Enter(1);
        WorkResult result = await Depth64Level02Async(state);
        state.AfterAwaitAndWork(1, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level02Async(RunState state)
    {
        state.Enter(2);
        WorkResult result = await Depth64Level03Async(state);
        state.AfterAwaitAndWork(2, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level03Async(RunState state)
    {
        state.Enter(3);
        WorkResult result = await Depth64Level04Async(state);
        state.AfterAwaitAndWork(3, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level04Async(RunState state)
    {
        state.Enter(4);
        WorkResult result = await Depth64Level05Async(state);
        state.AfterAwaitAndWork(4, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level05Async(RunState state)
    {
        state.Enter(5);
        WorkResult result = await Depth64Level06Async(state);
        state.AfterAwaitAndWork(5, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level06Async(RunState state)
    {
        state.Enter(6);
        WorkResult result = await Depth64Level07Async(state);
        state.AfterAwaitAndWork(6, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level07Async(RunState state)
    {
        state.Enter(7);
        WorkResult result = await Depth64Level08Async(state);
        state.AfterAwaitAndWork(7, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level08Async(RunState state)
    {
        state.Enter(8);
        WorkResult result = await Depth64Level09Async(state);
        state.AfterAwaitAndWork(8, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level09Async(RunState state)
    {
        state.Enter(9);
        WorkResult result = await Depth64Level10Async(state);
        state.AfterAwaitAndWork(9, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level10Async(RunState state)
    {
        state.Enter(10);
        WorkResult result = await Depth64Level11Async(state);
        state.AfterAwaitAndWork(10, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level11Async(RunState state)
    {
        state.Enter(11);
        WorkResult result = await Depth64Level12Async(state);
        state.AfterAwaitAndWork(11, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level12Async(RunState state)
    {
        state.Enter(12);
        WorkResult result = await Depth64Level13Async(state);
        state.AfterAwaitAndWork(12, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level13Async(RunState state)
    {
        state.Enter(13);
        WorkResult result = await Depth64Level14Async(state);
        state.AfterAwaitAndWork(13, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level14Async(RunState state)
    {
        state.Enter(14);
        WorkResult result = await Depth64Level15Async(state);
        state.AfterAwaitAndWork(14, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level15Async(RunState state)
    {
        state.Enter(15);
        WorkResult result = await Depth64Level16Async(state);
        state.AfterAwaitAndWork(15, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level16Async(RunState state)
    {
        state.Enter(16);
        WorkResult result = await Depth64Level17Async(state);
        state.AfterAwaitAndWork(16, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level17Async(RunState state)
    {
        state.Enter(17);
        WorkResult result = await Depth64Level18Async(state);
        state.AfterAwaitAndWork(17, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level18Async(RunState state)
    {
        state.Enter(18);
        WorkResult result = await Depth64Level19Async(state);
        state.AfterAwaitAndWork(18, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level19Async(RunState state)
    {
        state.Enter(19);
        WorkResult result = await Depth64Level20Async(state);
        state.AfterAwaitAndWork(19, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level20Async(RunState state)
    {
        state.Enter(20);
        WorkResult result = await Depth64Level21Async(state);
        state.AfterAwaitAndWork(20, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level21Async(RunState state)
    {
        state.Enter(21);
        WorkResult result = await Depth64Level22Async(state);
        state.AfterAwaitAndWork(21, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level22Async(RunState state)
    {
        state.Enter(22);
        WorkResult result = await Depth64Level23Async(state);
        state.AfterAwaitAndWork(22, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level23Async(RunState state)
    {
        state.Enter(23);
        WorkResult result = await Depth64Level24Async(state);
        state.AfterAwaitAndWork(23, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level24Async(RunState state)
    {
        state.Enter(24);
        WorkResult result = await Depth64Level25Async(state);
        state.AfterAwaitAndWork(24, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level25Async(RunState state)
    {
        state.Enter(25);
        WorkResult result = await Depth64Level26Async(state);
        state.AfterAwaitAndWork(25, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level26Async(RunState state)
    {
        state.Enter(26);
        WorkResult result = await Depth64Level27Async(state);
        state.AfterAwaitAndWork(26, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level27Async(RunState state)
    {
        state.Enter(27);
        WorkResult result = await Depth64Level28Async(state);
        state.AfterAwaitAndWork(27, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level28Async(RunState state)
    {
        state.Enter(28);
        WorkResult result = await Depth64Level29Async(state);
        state.AfterAwaitAndWork(28, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level29Async(RunState state)
    {
        state.Enter(29);
        WorkResult result = await Depth64Level30Async(state);
        state.AfterAwaitAndWork(29, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level30Async(RunState state)
    {
        state.Enter(30);
        WorkResult result = await Depth64Level31Async(state);
        state.AfterAwaitAndWork(30, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level31Async(RunState state)
    {
        state.Enter(31);
        WorkResult result = await Depth64Level32Async(state);
        state.AfterAwaitAndWork(31, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level32Async(RunState state)
    {
        state.Enter(32);
        WorkResult result = await Depth64Level33Async(state);
        state.AfterAwaitAndWork(32, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level33Async(RunState state)
    {
        state.Enter(33);
        WorkResult result = await Depth64Level34Async(state);
        state.AfterAwaitAndWork(33, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level34Async(RunState state)
    {
        state.Enter(34);
        WorkResult result = await Depth64Level35Async(state);
        state.AfterAwaitAndWork(34, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level35Async(RunState state)
    {
        state.Enter(35);
        WorkResult result = await Depth64Level36Async(state);
        state.AfterAwaitAndWork(35, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level36Async(RunState state)
    {
        state.Enter(36);
        WorkResult result = await Depth64Level37Async(state);
        state.AfterAwaitAndWork(36, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level37Async(RunState state)
    {
        state.Enter(37);
        WorkResult result = await Depth64Level38Async(state);
        state.AfterAwaitAndWork(37, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level38Async(RunState state)
    {
        state.Enter(38);
        WorkResult result = await Depth64Level39Async(state);
        state.AfterAwaitAndWork(38, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level39Async(RunState state)
    {
        state.Enter(39);
        WorkResult result = await Depth64Level40Async(state);
        state.AfterAwaitAndWork(39, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level40Async(RunState state)
    {
        state.Enter(40);
        WorkResult result = await Depth64Level41Async(state);
        state.AfterAwaitAndWork(40, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level41Async(RunState state)
    {
        state.Enter(41);
        WorkResult result = await Depth64Level42Async(state);
        state.AfterAwaitAndWork(41, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level42Async(RunState state)
    {
        state.Enter(42);
        WorkResult result = await Depth64Level43Async(state);
        state.AfterAwaitAndWork(42, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level43Async(RunState state)
    {
        state.Enter(43);
        WorkResult result = await Depth64Level44Async(state);
        state.AfterAwaitAndWork(43, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level44Async(RunState state)
    {
        state.Enter(44);
        WorkResult result = await Depth64Level45Async(state);
        state.AfterAwaitAndWork(44, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level45Async(RunState state)
    {
        state.Enter(45);
        WorkResult result = await Depth64Level46Async(state);
        state.AfterAwaitAndWork(45, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level46Async(RunState state)
    {
        state.Enter(46);
        WorkResult result = await Depth64Level47Async(state);
        state.AfterAwaitAndWork(46, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level47Async(RunState state)
    {
        state.Enter(47);
        WorkResult result = await Depth64Level48Async(state);
        state.AfterAwaitAndWork(47, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level48Async(RunState state)
    {
        state.Enter(48);
        WorkResult result = await Depth64Level49Async(state);
        state.AfterAwaitAndWork(48, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level49Async(RunState state)
    {
        state.Enter(49);
        WorkResult result = await Depth64Level50Async(state);
        state.AfterAwaitAndWork(49, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level50Async(RunState state)
    {
        state.Enter(50);
        WorkResult result = await Depth64Level51Async(state);
        state.AfterAwaitAndWork(50, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level51Async(RunState state)
    {
        state.Enter(51);
        WorkResult result = await Depth64Level52Async(state);
        state.AfterAwaitAndWork(51, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level52Async(RunState state)
    {
        state.Enter(52);
        WorkResult result = await Depth64Level53Async(state);
        state.AfterAwaitAndWork(52, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level53Async(RunState state)
    {
        state.Enter(53);
        WorkResult result = await Depth64Level54Async(state);
        state.AfterAwaitAndWork(53, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level54Async(RunState state)
    {
        state.Enter(54);
        WorkResult result = await Depth64Level55Async(state);
        state.AfterAwaitAndWork(54, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level55Async(RunState state)
    {
        state.Enter(55);
        WorkResult result = await Depth64Level56Async(state);
        state.AfterAwaitAndWork(55, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level56Async(RunState state)
    {
        state.Enter(56);
        WorkResult result = await Depth64Level57Async(state);
        state.AfterAwaitAndWork(56, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level57Async(RunState state)
    {
        state.Enter(57);
        WorkResult result = await Depth64Level58Async(state);
        state.AfterAwaitAndWork(57, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level58Async(RunState state)
    {
        state.Enter(58);
        WorkResult result = await Depth64Level59Async(state);
        state.AfterAwaitAndWork(58, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level59Async(RunState state)
    {
        state.Enter(59);
        WorkResult result = await Depth64Level60Async(state);
        state.AfterAwaitAndWork(59, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level60Async(RunState state)
    {
        state.Enter(60);
        WorkResult result = await Depth64Level61Async(state);
        state.AfterAwaitAndWork(60, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level61Async(RunState state)
    {
        state.Enter(61);
        WorkResult result = await Depth64Level62Async(state);
        state.AfterAwaitAndWork(61, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level62Async(RunState state)
    {
        state.Enter(62);
        WorkResult result = await Depth64Level63Async(state);
        state.AfterAwaitAndWork(62, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level63Async(RunState state)
    {
        state.Enter(63);
        WorkResult result = await Depth64Level64Async(state);
        state.AfterAwaitAndWork(63, 1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> Depth64Level64Async(RunState state)
    {
        state.Enter(64);
        state.SuspendedRound = 0;
        await state.Gates[0].Task;
        state.AfterAwaitAndWork(64, 0);
        state.SuspendedRound = 1;
        await state.Gates[1].Task;
        return state.AfterAwaitAndWork(64, 1);
    }
}
