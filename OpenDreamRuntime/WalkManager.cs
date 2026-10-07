using System.Threading;
using OpenDreamRuntime.Map;
using OpenDreamRuntime.Objects.Types;
using OpenDreamRuntime.Procs;
using OpenDreamRuntime.Procs.Native;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime;

/// <summary>
///     Handles walking movables.<br />
///     walk_towards(), walk_to(), walk_away(), etc.
/// </summary>
public sealed partial class WalkManager {
    private readonly Dictionary<DreamObjectMovable, CancellationTokenSource> _walkTasks = new();
    [Dependency] private AtomManager _atomManager = default!;
    [Dependency] private DreamManager _dreamManager = default!;
    [Dependency] private IDreamMapManager _dreamMapManager = default!;
    [Dependency] private ProcScheduler _scheduler = default!;

    /// <summary>
    ///     Stop any active walks on a movable
    /// </summary>
    public void StopWalks(DreamObjectMovable movable) {
        if (_walkTasks.Remove(movable, out CancellationTokenSource? walk)) {
            walk.Cancel();
            movable.DecRef();
        }
    }

    /// <summary>
    ///     Walk in the specified direction Dir continuously.
    /// </summary>
    public void StartWalk(DreamObjectMovable movable, int dir, int lag, int speed) {
        // TODO: Implement speed. Speed=0 uses Ref.step_size
        StopWalks(movable);

        lag = Math.Max(lag, 1); // Minimum of 1 tick lag

        CancellationTokenSource cancelSource = new();
        _walkTasks[movable] = cancelSource;
        movable.IncRef();

        DreamThread.Run($"walk {dir}", async state => {
            DreamProc moveProc = movable.GetProc("Move");

            while (true) {
                await _scheduler.CreateDelayTicks(lag);
                if (cancelSource.IsCancellationRequested)
                    break;

                DreamObjectTurf? newLoc =
                    DreamProcNativeHelpers.GetStep(_atomManager, _dreamMapManager, movable, (AtomDirection)dir);
                await state.CallNoWait(moveProc, movable, null, new DreamValue(newLoc), new DreamValue(dir));
            }

            return DreamValue.Null;
        }).Dispose();
    }

    /// <summary>
    ///     Walk in a random direction continuously.
    /// </summary>
    public void StartWalkRand(DreamObjectMovable movable, int lag, int speed) {
        // TODO: Implement speed. Speed=0 uses Ref.step_size
        StopWalks(movable);

        lag = Math.Max(lag, 1); // Minimum of 1 tick lag

        CancellationTokenSource cancelSource = new();
        _walkTasks[movable] = cancelSource;
        movable.IncRef();

        DreamThread.Run("walk_rand", async state => {
            DreamProc moveProc = movable.GetProc("Move");

            while (true) {
                await _scheduler.CreateDelayTicks(lag);
                if (cancelSource.IsCancellationRequested)
                    break;

                AtomDirection dir = DreamProcNativeHelpers.GetRandomDirection(_dreamManager);
                DreamObjectTurf? newLoc = DreamProcNativeHelpers.GetStep(_atomManager, _dreamMapManager, movable, dir);
                await state.CallNoWait(moveProc, movable, null, new DreamValue(newLoc), new DreamValue((int)dir));
            }

            return DreamValue.Null;
        }).Dispose();
    }

    /// <summary>
    ///     Walk towards the target with no pathfinding taken into account
    /// </summary>
    public void StartWalkTowards(DreamObjectMovable movable, DreamObjectAtom target, int lag, int speed) {
        // TODO: Implement speed. Speed=0 uses Ref.step_size
        StopWalks(movable);

        lag = Math.Max(lag, 1); // Minimum of 1 tick lag

        CancellationTokenSource cancelSource = new();
        _walkTasks[movable] = cancelSource;
        movable.IncRef();

        DreamThread.Run($"walk_towards {movable}", async state => {
            DreamProc moveProc = movable.GetProc("Move");

            while (true) {
                await _scheduler.CreateDelayTicks(lag);
                if (cancelSource.IsCancellationRequested)
                    break;

                AtomDirection dir = DreamProcNativeHelpers.GetDir(_atomManager, movable, target);
                if (dir == AtomDirection.None)
                    continue;

                DreamObjectTurf? newLoc = DreamProcNativeHelpers.GetStep(_atomManager, _dreamMapManager, movable, dir);
                await state.CallNoWait(moveProc, movable, null, new DreamValue(newLoc), new DreamValue((int)dir));
            }

            return DreamValue.Null;
        }).Dispose();
    }

    /// <summary>
    ///     Walk towards the target with pathfinding taken into account
    /// </summary>
    public void StartWalkTo(DreamObjectMovable movable, DreamObjectAtom target, int min, int lag, int speed) {
        // TODO: Implement speed. Speed=0 uses Ref.step_size
        StopWalks(movable);

        lag = Math.Max(lag, 1); // Minimum of 1 tick lag

        CancellationTokenSource cancelSource = new();
        _walkTasks[movable] = cancelSource;
        movable.IncRef();

        DreamThread.Run($"walk_to {movable}", async state => {
            DreamProc moveProc = movable.GetProc("Move");

            while (true) {
                await _scheduler.CreateDelayTicks(lag);
                if (cancelSource.IsCancellationRequested)
                    break;

                (int X, int Y, int Z) currentLoc = _atomManager.GetAtomPosition(movable);
                (int X, int Y, int Z) targetLoc = _atomManager.GetAtomPosition(target);
                ViewRange worldView = _dreamManager.WorldInstance.DefaultView;
                int maxSteps = Math.Max(worldView.Width, worldView.Height) - 1;
                IEnumerable<AtomDirection>
                    steps = _dreamMapManager.CalculateSteps(currentLoc, targetLoc, min, maxSteps);
                using IEnumerator<AtomDirection> enumerator = steps.GetEnumerator();
                if (!enumerator.MoveNext()) // No more steps to take
                    break;

                AtomDirection dir = enumerator.Current;
                DreamObjectTurf? newLoc = DreamProcNativeHelpers.GetStep(_atomManager, _dreamMapManager, movable, dir);
                await state.CallNoWait(moveProc, movable, null, new DreamValue(newLoc), new DreamValue((int)dir));
            }

            return DreamValue.Null;
        }).Dispose();
    }
}
