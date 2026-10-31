using MediatR;

namespace Whisper.MediatR.UnitTests;

// MediatR 12.5 added a CancellationToken parameter to RequestHandlerDelegate; this keeps the
// tests compiling against both shapes when they run with -p:MediatRVersion=x.y.z.
internal static class Next
{
    public static RequestHandlerDelegate<TResponse> From<TResponse>(Func<Task<TResponse>> handler)
#if MEDIATR_NEXT_TAKES_CANCELLATION_TOKEN
        => _ => handler();
#else
        => () => handler();
#endif
}
