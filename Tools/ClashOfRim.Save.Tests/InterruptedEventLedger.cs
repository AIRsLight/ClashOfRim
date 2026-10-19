using System.Reflection;
using System.Runtime.ExceptionServices;
using AIRsLight.ClashOfRim.Events;

public class InterruptedEventLedger : DispatchProxy
{
    public IAuthoritativeEventLedger Inner = null!;
    public Action? BeforeBatch, AfterBatch;
    public Action? BeforeAppend, AfterAppend;
    public Action<string>? OnCall;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        OnCall?.Invoke(targetMethod!.Name);
        bool batch = targetMethod!.Name == nameof(IAuthoritativeEventLedger.ApplyBatch);
        bool append = targetMethod.Name == nameof(IAuthoritativeEventLedger.Append);
        if (batch) BeforeBatch?.Invoke();
        if (append) BeforeAppend?.Invoke();
        object? result;
        try { result = targetMethod.Invoke(Inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        if (batch) AfterBatch?.Invoke();
        if (append) AfterAppend?.Invoke();
        return result;
    }
}
