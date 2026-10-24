using System.Runtime.CompilerServices;
using System.Threading.Tasks;
namespace WpfAsyncScenarios;
internal static partial class Runner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel01Async(RunState state)
    {
        state.Enter(1);
        WorkResult result = await RolloverLevel02Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(1);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel02Async(RunState state)
    {
        state.Enter(2);
        WorkResult result = await RolloverLevel03Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(2);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel03Async(RunState state)
    {
        state.Enter(3);
        WorkResult result = await RolloverLevel04Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(3);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel04Async(RunState state)
    {
        state.Enter(4);
        WorkResult result = await RolloverLevel05Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(4);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel05Async(RunState state)
    {
        state.Enter(5);
        WorkResult result = await RolloverLevel06Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(5);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel06Async(RunState state)
    {
        state.Enter(6);
        WorkResult result = await RolloverLevel07Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(6);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel07Async(RunState state)
    {
        state.Enter(7);
        WorkResult result = await RolloverLevel08Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(7);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel08Async(RunState state)
    {
        state.Enter(8);
        WorkResult result = await RolloverLevel09Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(8);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel09Async(RunState state)
    {
        state.Enter(9);
        WorkResult result = await RolloverLevel10Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(9);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel10Async(RunState state)
    {
        state.Enter(10);
        WorkResult result = await RolloverLevel11Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(10);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel11Async(RunState state)
    {
        state.Enter(11);
        WorkResult result = await RolloverLevel12Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(11);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel12Async(RunState state)
    {
        state.Enter(12);
        WorkResult result = await RolloverLevel13Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(12);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel13Async(RunState state)
    {
        state.Enter(13);
        WorkResult result = await RolloverLevel14Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(13);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel14Async(RunState state)
    {
        state.Enter(14);
        WorkResult result = await RolloverLevel15Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(14);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel15Async(RunState state)
    {
        state.Enter(15);
        WorkResult result = await RolloverLevel16Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(15);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel16Async(RunState state)
    {
        state.Enter(16);
        WorkResult result = await RolloverLevel17Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(16);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel17Async(RunState state)
    {
        state.Enter(17);
        WorkResult result = await RolloverLevel18Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(17);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel18Async(RunState state)
    {
        state.Enter(18);
        WorkResult result = await RolloverLevel19Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(18);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel19Async(RunState state)
    {
        state.Enter(19);
        WorkResult result = await RolloverLevel20Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(19);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel20Async(RunState state)
    {
        state.Enter(20);
        WorkResult result = await RolloverLevel21Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(20);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel21Async(RunState state)
    {
        state.Enter(21);
        WorkResult result = await RolloverLevel22Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(21);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel22Async(RunState state)
    {
        state.Enter(22);
        WorkResult result = await RolloverLevel23Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(22);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel23Async(RunState state)
    {
        state.Enter(23);
        WorkResult result = await RolloverLevel24Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(23);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel24Async(RunState state)
    {
        state.Enter(24);
        WorkResult result = await RolloverLevel25Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(24);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel25Async(RunState state)
    {
        state.Enter(25);
        WorkResult result = await RolloverLevel26Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(25);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel26Async(RunState state)
    {
        state.Enter(26);
        WorkResult result = await RolloverLevel27Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(26);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel27Async(RunState state)
    {
        state.Enter(27);
        WorkResult result = await RolloverLevel28Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(27);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel28Async(RunState state)
    {
        state.Enter(28);
        WorkResult result = await RolloverLevel29Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(28);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel29Async(RunState state)
    {
        state.Enter(29);
        WorkResult result = await RolloverLevel30Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(29);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel30Async(RunState state)
    {
        state.Enter(30);
        WorkResult result = await RolloverLevel31Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(30);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel31Async(RunState state)
    {
        state.Enter(31);
        WorkResult result = await RolloverLevel32Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(31);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel32Async(RunState state)
    {
        state.Enter(32);
        WorkResult result = await RolloverLevel33Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(32);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel33Async(RunState state)
    {
        state.Enter(33);
        WorkResult result = await RolloverLevel34Async(state).ConfigureAwait(false);
        state.AfterAwaitAndWork(33);
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<WorkResult> RolloverLevel34Async(RunState state)
    {
        state.Enter(34);
        await state.Gate.Task.ConfigureAwait(false);
        return state.AfterAwaitAndWork(34);
    }
}
