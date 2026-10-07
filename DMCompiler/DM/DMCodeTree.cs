using System.Diagnostics.CodeAnalysis;
using DMCompiler.Compiler;
using DMCompiler.DM.Builders;

namespace DMCompiler.DM;

/// <summary>
///     A representation of all the types, procs, and vars defined in the tree.<br />
///     Important in the role of defining everything & initializing statics in the correct order.
/// </summary>
// TODO: "/var" vs "var" has a different init order (same for procs)
// TODO: Path elements like /static and /global are grouped together
internal partial class DMCodeTree {
    private readonly DMCompiler _compiler;
    private readonly Dictionary<DreamPath, ProcNode> _newProcs = new();
    private readonly Dictionary<DreamPath, DreamPath> _parentTypes = new();
    private readonly HashSet<INode> _waitingNodes = new();
    private int _currentPass;
    private ObjectNode? _dmStandardRoot;
    private ObjectNode _root;

    public DMCodeTree(DMCompiler compiler) {
        // Yep, not _dmStandardRoot
        // They get switched in FinishDMStandard()
        _root = new ObjectNode(this, "/ (DMStandard)", DreamPath.Root);

        _compiler = compiler;
    }

    public void DefineEverything() {
        if (_dmStandardRoot == null)
            FinishDMStandard();

        void Pass(ObjectNode root) {
            foreach (INode node in TraverseNodes(root)) {
                bool successful = (node is ObjectNode objectNode && objectNode.TryDefineType(_compiler)) ||
                                  (node is ProcNode procNode && procNode.TryDefineProc(_compiler)) ||
                                  (node is VarNode varNode && varNode.TryDefineVar(_compiler, _currentPass));

                if (successful)
                    _waitingNodes.Remove(node);
            }
        }

        // Pass 0
        DMExpressionBuilder.ScopeOperatorEnabled = false;
        Pass(_root);
        Pass(_dmStandardRoot!);

        int lastCount;
        do {
            _currentPass++;
            lastCount = _waitingNodes.Count;

            Pass(_root);
            Pass(_dmStandardRoot!);
        } while (_waitingNodes.Count < lastCount && _waitingNodes.Count > 0);

        // Scope operator pass
        DMExpressionBuilder.ScopeOperatorEnabled = true;
        Pass(_root);
        Pass(_dmStandardRoot!);

        // If there exists vars that didn't successfully compile, emit their errors
        foreach (INode node in _waitingNodes) {
            if (node is not VarNode varNode) // TODO: If a type or proc fails?
                continue;
            if (varNode.LastError == null)
                continue;

            _compiler.Emit(WarningCode.ItemDoesntExist, varNode.LastError.Location,
                varNode.LastError.Message);
        }

        _compiler.GlobalInitProc.ResolveLabels();
    }

    public void FinishDMStandard() {
        _dmStandardRoot = _root;
        _root = new ObjectNode(this, "/", DreamPath.Root);
    }

    public void AddType(DreamPath type) {
        GetDMObjectNode(type); // Add it to our tree
    }

    public DreamPath? UpwardSearch(DMObject start, DreamPath search) {
        DreamPath currentPath = start.Path;

        search.Type = DreamPath.PathType.Relative;

        while (true) {
            TypeNode node = GetDMObjectNode(currentPath);

            for (var i = 0; i < search.Elements.Length; i++) {
                string element = search.Elements[i];
                if (element == "verb")
                    element = "proc"; // TODO: Separate proc and verb on the code tree

                if (!node.TryGetChild(element, out TypeNode? child))
                    break;

                if (i == search.Elements.Length - 1)
                    return currentPath.AddToPath(search.PathString);

                node = child;
            }

            if (currentPath == DreamPath.Root)
                return null;
            currentPath = currentPath.FromElements(0, -2);
        }
    }

    public void Print() {
        PrintNode(_root);
        if (_dmStandardRoot != null)
            PrintNode(_dmStandardRoot);
    }

    private void PrintNode(INode node, int level = 0) {
        if (node is TypeNode typeNode) {
            Console.Write(new string('\t', level));
            Console.WriteLine(typeNode);

            foreach (INode child in typeNode.Children) PrintNode(child, level + 1);
        } else {
            Console.Write(new string('\t', level));
            Console.WriteLine(node);
        }
    }

    private ObjectNode GetDMObjectNode(DreamPath path) {
        ObjectNode node = _root;

        for (var i = 0; i < path.Elements.Length; i++) {
            string element = path.Elements[i];
            if (!node.TryGetChild(element, out TypeNode? childNode)) {
                DreamPath creating = path.FromElements(0, i + 1);

                _compiler.VerbosePrint($"Adding {creating} to the code tree");
                childNode = new ObjectNode(this, element, creating);
                node.Children.Add(childNode);
                _waitingNodes.Add(childNode);
            }

            if (childNode is not ObjectNode objectNode)
                break;

            node = objectNode;
        }

        return node;
    }

    private IEnumerable<INode> TraverseNodes(TypeNode from) {
        yield return from;

        foreach (INode child in from.Children) {
            yield return child;

            if (child is TypeNode typeNode) {
                using IEnumerator<INode> children = TraverseNodes(typeNode).GetEnumerator();
                while (children.MoveNext())
                    yield return children.Current;
            }
        }
    }

    private interface INode;

    private class TypeNode(string name) : INode {
        public readonly List<INode> Children = new();

        private readonly string _name = name;

        public bool TryGetChild(string name, [NotNullWhen(true)] out TypeNode? child) {
            foreach (INode ourChild in Children) {
                if (ourChild is not TypeNode typeNode)
                    continue;

                if (typeNode._name == name) {
                    child = typeNode;
                    return true;
                }
            }

            child = null;
            return false;
        }

        public override string ToString() {
            return _name;
        }
    }

    private class ObjectNode(DMCodeTree codeTree, string name, DreamPath type) : TypeNode(name) {
        private bool _defined;
        private ProcsNode? _procs;

        public bool TryDefineType(DMCompiler compiler) {
            if (_defined)
                return true;

            DMObject? explicitParent = null;
            if (codeTree._parentTypes.TryGetValue(type, out DreamPath parentType) &&
                !compiler.DMObjectTree.TryGetDMObject(parentType, out explicitParent))
                return false; // Parent type isn't ready yet

            _defined = true;

            DMObject dmObject = compiler.DMObjectTree.GetOrCreateDMObject(type);
            if (explicitParent != null) {
                dmObject.Parent = explicitParent;
                codeTree._parentTypes.Remove(type);
            }

            if (codeTree._newProcs.Remove(type, out ProcNode? newProcNode))
                newProcNode.TryDefineProc(compiler);

            return true;
        }

        public ProcsNode AddProcsNode() {
            if (_procs is null) {
                _procs = new ProcsNode();
                Children.Add(_procs);
            }

            return _procs;
        }
    }
}
