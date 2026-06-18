using System;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Robust.Server.ViewVariables;

/// <summary>
/// Raised on the server after a client successfully modifies a remote VV member.
/// </summary>
public sealed class ViewVariablesModifyRemoteEvent : EntityEventArgs
{
    public ViewVariablesModifyRemoteEvent(
        NetUserId playerUser,
        uint sessionId,
        object target,
        Type targetType,
        string? propertyPath,
        bool hasOldValue,
        object? oldValue,
        object? newValue,
        bool reinterpretedValue)
    {
        PlayerUser = playerUser;
        SessionId = sessionId;
        Target = target;
        TargetType = targetType;
        PropertyPath = propertyPath;
        HasOldValue = hasOldValue;
        OldValue = oldValue;
        NewValue = newValue;
        ReinterpretedValue = reinterpretedValue;
    }

    public NetUserId PlayerUser { get; }
    public uint SessionId { get; }
    public object Target { get; }
    public Type TargetType { get; }
    public string? PropertyPath { get; }
    public bool HasOldValue { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }
    public bool ReinterpretedValue { get; }
}
