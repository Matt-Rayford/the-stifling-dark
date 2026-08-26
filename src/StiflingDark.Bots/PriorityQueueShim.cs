namespace System.Collections.Generic;

/// <summary>
/// A netstandard2.1 stand-in for .NET 6+'s <c>System.Collections.Generic.PriorityQueue&lt;,&gt;</c>,
/// covering only the default-comparer <c>Enqueue</c>/<c>TryDequeue</c> pair that
/// <see cref="StiflingDark.Bots.InvestigatorTeam"/>'s Dijkstra field uses. The heap shape (4-ary,
/// same grow/move-up/move-down arithmetic as the BCL) is copied deliberately: a different heap
/// produces a different tie-break order among equal-cost spaces, which would silently change the
/// bots' pathing and diverge them from the net8.0 build this replaces.
/// </summary>
internal sealed class PriorityQueue<TElement, TPriority>
{
    private const int Arity = 4;
    private const int Log2Arity = 2;

    private (TElement Element, TPriority Priority)[] _nodes = Array.Empty<(TElement, TPriority)>();
    private int _size;

    public void Enqueue(TElement element, TPriority priority)
    {
        int index = _size++;
        if (_nodes.Length == index)
        {
            Grow(index + 1);
        }
        MoveUp((element, priority), index);
    }

    public bool TryDequeue(out TElement element, out TPriority priority)
    {
        if (_size == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }
        (element, priority) = _nodes[0];
        RemoveRootNode();
        return true;
    }

    private void RemoveRootNode()
    {
        int lastIndex = --_size;
        if (lastIndex > 0)
        {
            (TElement Element, TPriority Priority) lastNode = _nodes[lastIndex];
            MoveDown(lastNode, 0);
        }
        _nodes[lastIndex] = default;
    }

    private void Grow(int minCapacity)
    {
        const int MinimumGrow = 4;
        int newCapacity = 2 * _nodes.Length;
        newCapacity = Math.Max(newCapacity, _nodes.Length + MinimumGrow);
        newCapacity = Math.Max(newCapacity, minCapacity);
        Array.Resize(ref _nodes, newCapacity);
    }

    private void MoveUp((TElement Element, TPriority Priority) node, int nodeIndex)
    {
        (TElement Element, TPriority Priority)[] nodes = _nodes;
        while (nodeIndex > 0)
        {
            int parentIndex = (nodeIndex - 1) >> Log2Arity;
            (TElement Element, TPriority Priority) parent = nodes[parentIndex];
            if (Comparer<TPriority>.Default.Compare(node.Priority, parent.Priority) < 0)
            {
                nodes[nodeIndex] = parent;
                nodeIndex = parentIndex;
            }
            else
            {
                break;
            }
        }
        nodes[nodeIndex] = node;
    }

    private void MoveDown((TElement Element, TPriority Priority) node, int nodeIndex)
    {
        (TElement Element, TPriority Priority)[] nodes = _nodes;
        int size = _size;
        int i;
        while ((i = (nodeIndex << Log2Arity) + 1) < size)
        {
            (TElement Element, TPriority Priority) minChild = nodes[i];
            int minChildIndex = i;
            int upperBound = Math.Min(i + Arity, size);
            while (++i < upperBound)
            {
                (TElement Element, TPriority Priority) nextChild = nodes[i];
                if (Comparer<TPriority>.Default.Compare(nextChild.Priority, minChild.Priority) < 0)
                {
                    minChild = nextChild;
                    minChildIndex = i;
                }
            }
            if (Comparer<TPriority>.Default.Compare(node.Priority, minChild.Priority) <= 0)
            {
                break;
            }
            nodes[nodeIndex] = minChild;
            nodeIndex = minChildIndex;
        }
        nodes[nodeIndex] = node;
    }
}
