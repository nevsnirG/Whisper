namespace Whisper;

internal sealed class DomainEventScopeStack
{
    public int Count => _scopeStack.Count;
    private readonly Stack<WeakReference<DomainEventScope>> _scopeStack;

    public DomainEventScopeStack()
    {
        _scopeStack = new();
    }

    public void Push(DomainEventScope scope)
    {
        _scopeStack.Push(new(scope, false));
    }

    public DomainEventScope? Peek()
    {
        if (_scopeStack.TryPeek(out var deepestScopeRef))
        {
            if (deepestScopeRef.TryGetTarget(out var deepestScope))
                return deepestScope;
            else
            {
                _scopeStack.Pop();
                return Peek();
            }
        }
        else
        {
            return null;
        }
    }

    public DomainEventScope? Pop()
    {
        if (_scopeStack.TryPop(out var deepestScopeRef))
        {
            if (deepestScopeRef.TryGetTarget(out var deepestScope))
                return deepestScope;
            else
            {
                return Pop();
            }
        }
        else
        {
            return null;
        }
    }
}