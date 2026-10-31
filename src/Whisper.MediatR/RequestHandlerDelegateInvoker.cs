using System.Linq.Expressions;
using MediatR;

namespace Whisper.MediatR;

/// <summary>
/// Calls <see cref="RequestHandlerDelegate{TResponse}"/> through the Invoke signature of the MediatR version loaded at
/// runtime. MediatR 12.5 added a <see cref="CancellationToken"/> parameter to Invoke, so a direct <c>next()</c> compiled
/// against an older version throws <see cref="MissingMethodException"/> on 12.5 and later (and the reverse when compiled
/// against a newer one). Binding at runtime keeps one build working with every MediatR version from 12.0.1 on.
/// </summary>
internal static class RequestHandlerDelegateInvoker<TResponse>
{
    private static readonly Func<RequestHandlerDelegate<TResponse>, CancellationToken, Task<TResponse>> InvokeNext = Build();

    public static Task<TResponse> Invoke(RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => InvokeNext(next, cancellationToken);

    private static Func<RequestHandlerDelegate<TResponse>, CancellationToken, Task<TResponse>> Build()
    {
        var next = Expression.Parameter(typeof(RequestHandlerDelegate<TResponse>), "next");
        var cancellationToken = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var invoke = typeof(RequestHandlerDelegate<TResponse>).GetMethod(nameof(Action.Invoke))!;

        Expression call = invoke.GetParameters() switch
        {
            [] => Expression.Call(next, invoke),
            [var parameter] when parameter.ParameterType == typeof(CancellationToken) => Expression.Call(next, invoke, cancellationToken),
            _ => throw new NotSupportedException(
                $"Unsupported {typeof(RequestHandlerDelegate<TResponse>).Name} signature in MediatR {invoke.DeclaringType!.Assembly.GetName().Version}."),
        };

        return Expression.Lambda<Func<RequestHandlerDelegate<TResponse>, CancellationToken, Task<TResponse>>>(call, next, cancellationToken)
            .Compile();
    }
}
