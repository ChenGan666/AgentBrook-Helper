namespace AgentBrook.Privacy.Detectors;

/// <summary>Aho-Corasick 多模式自动机（词典匹配核心）。Scan 逐位置走 fail 链枚举全部终态（长→短由调用方排序）。</summary>
internal sealed class AhoCorasick<TPayload>
{
    public readonly record struct AcHit(int End, int Length, TPayload Payload);

    private sealed class Node
    {
        public Dictionary<char, int> Next = new();
        public int Fail;
        public TPayload? Payload;        // 终态 payload
        public int PatternLength;        // 0 = 非终态（Build 后含 fail 链传播）
    }

    private readonly List<Node> _nodes = [new()];
    private bool _built;

    public void Add(string pattern, TPayload payload)
    {
        if (_built)
        {
            throw new InvalidOperationException("Build 之后不可再 Add。");
        }
        var node = 0;
        foreach (var ch in pattern)
        {
            if (!_nodes[node].Next.TryGetValue(ch, out var next))
            {
                next = _nodes.Count;
                _nodes[node].Next[ch] = next;
                _nodes.Add(new Node());
            }
            node = next;
        }
        _nodes[node].Payload = payload;
        _nodes[node].PatternLength = pattern.Length;
    }

    public void Build()
    {
        var queue = new Queue<int>();
        foreach (var (_, child) in _nodes[0].Next)
        {
            _nodes[child].Fail = 0;
            queue.Enqueue(child);
        }
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            // 传播最长终态：fail 深度更浅、已在更早轮次处理（BFS）
            if (_nodes[cur].PatternLength == 0)
            {
                _nodes[cur].PatternLength = _nodes[_nodes[cur].Fail].PatternLength;
                _nodes[cur].Payload = _nodes[_nodes[cur].Fail].Payload;
            }
            foreach (var (ch, child) in _nodes[cur].Next)
            {
                var f = _nodes[cur].Fail;
                while (f != 0 && !_nodes[f].Next.TryGetValue(ch, out _))
                {
                    f = _nodes[f].Fail;
                }
                _nodes[child].Fail = _nodes[f].Next.TryGetValue(ch, out var target) && target != child
                    ? target
                    : 0;
                queue.Enqueue(child);
            }
        }
        _built = true;
    }

    /// <summary>扫描文本，产出全部命中（同一终态沿 fail 链给出更长→更短的全部后缀模式）。</summary>
    public IEnumerable<AcHit> Scan(string text)
    {
        if (!_built)
        {
            throw new InvalidOperationException("请先调用 Build。");
        }
        var node = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            while (node != 0 && !_nodes[node].Next.TryGetValue(ch, out _))
            {
                node = _nodes[node].Fail;
            }
            node = _nodes[node].Next.TryGetValue(ch, out var next) ? next : 0;
            for (var n = node; n != 0; n = _nodes[n].Fail)
            {
                if (_nodes[n].PatternLength > 0)
                {
                    yield return new AcHit(i + 1, _nodes[n].PatternLength, _nodes[n].Payload!);
                }
            }
        }
    }
}
