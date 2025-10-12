namespace Hermes;

internal sealed class DomainEventScopeStack
{
    public int Count => _scopeStack.Count;
    private readonly Stack<WeakReference<IDomainEventScope>> _scopeStack;

    public DomainEventScopeStack()
    {
        _scopeStack = new();
    }

    public void Push(IDomainEventScope scope)
    {
        _scopeStack.Push(new(scope, false));
    }

    public IDomainEventScope? Peek()
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

    public IDomainEventScope? Pop()
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