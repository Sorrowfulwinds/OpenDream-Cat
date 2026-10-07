using System.Diagnostics.CodeAnalysis;
using OpenDreamRuntime.Procs.Native;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime.Map;

public partial class DreamMapManager {
    public IEnumerable<AtomDirection> CalculateSteps((int X, int Y, int Z) loc, (int X, int Y, int Z) dest,
        int targetDistance, int maxSteps) {
        int z = loc.Z;
        if (z != dest.Z) // Different Z-levels are unreachable
            yield break;

        HashSet<PathFindNode> explored = new();
        Queue<PathFindNode> toExplore = new();

        toExplore.Enqueue(PathFindNode.GetNode(loc.X, loc.Y));

        void Explore(PathFindNode current, int offsetX, int offsetY) {
            int nextX = current.X + offsetX;
            int nextY = current.Y + offsetY;
            if (nextX < 1 || nextX > Size.X || nextY < 1 || nextY > Size.Y)
                return; // This is outside of map bounds
            if (current.NeededSteps >= maxSteps)
                return; // We won't search any further than maxSteps

            PathFindNode next = PathFindNode.GetNode(nextX, nextY);
            if (explored.Contains(next))
                return;
            if (!TryGetCellAt(new Vector2i(next.X, next.Y), z, out IDreamMapManager.Cell? cell) || cell.Turf.IsDense)
                return;

            if (!toExplore.Contains(next))
                toExplore.Enqueue(next);
            else if (next.NeededSteps >= current.NeededSteps + 1)
                return;

            next.NeededSteps = current.NeededSteps + 1;
            next.Parent = current;
        }

        while (toExplore.TryDequeue(out PathFindNode? node)) {
            int distX = node.X - dest.X;
            int distY = node.Y - dest.Y;
            if ((int)Math.Sqrt(distX * distX + distY * distY) <= targetDistance) { // Path to the destination was found
                Stack<AtomDirection> path = new(node.NeededSteps);

                while (node.Parent != null) {
                    AtomDirection stepDir =
                        DreamProcNativeHelpers.GetDir((node.Parent.X, node.Parent.Y, z), (node.X, node.Y, z));

                    node = node.Parent;
                    path.Push(stepDir);
                }

                while (path.TryPop(out AtomDirection step)) yield return step;

                break;
            }

            explored.Add(node);
            Explore(node, 1, 0);
            Explore(node, 1, 1);
            Explore(node, 0, 1);
            Explore(node, -1, 1);
            Explore(node, -1, 0);
            Explore(node, -1, -1);
            Explore(node, 0, -1);
            Explore(node, 1, -1);
        }

        foreach (PathFindNode node in explored)
            node.Dispose();
        foreach (PathFindNode node in toExplore)
            node.Dispose();
    }

    private sealed class PathFindNode : IDisposable, IEquatable<PathFindNode> {
        private static readonly Stack<PathFindNode> Pool = new();
        public int NeededSteps;
        public PathFindNode? Parent;

        public int X, Y;

        public void Dispose() {
            Pool.Push(this);
        }

        public bool Equals(PathFindNode? other) {
            if (other is null)
                return false;
            return X == other.X && Y == other.Y;
        }

        public static PathFindNode GetNode(int x, int y) {
            if (!Pool.TryPop(out PathFindNode? node)) node = new PathFindNode();

            node.Parent = null;
            node.X = x;
            node.Y = y;
            node.NeededSteps = 0;
            return node;
        }

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public override int GetHashCode() {
            return HashCode.Combine(X, Y);
        }
    }
}
