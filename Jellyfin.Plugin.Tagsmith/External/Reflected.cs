using System.Reflection;

namespace Jellyfin.Plugin.Tagsmith.External;

/// <summary>
/// The little bits of reflection the external sources share. Kept small and dumb on
/// purpose: every call site knows exactly which server or plugin version it transcribed its
/// signatures from, and anything unexpected surfaces as null rather than an exception.
/// </summary>
public static class Reflected
{
    /// <summary>
    /// Reads a public instance property off an object whose type we cannot name at compile
    /// time. Returns null when the property does not exist.
    /// </summary>
    public static object? Get(object? target, string property) =>
        target?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    /// <summary>
    /// Reads a string property.
    /// </summary>
    public static string? GetString(object? target, string property) => Get(target, property) as string;

    /// <summary>
    /// Awaits a reflected <c>Task&lt;T&gt;</c> and returns its result as an object.
    /// </summary>
    /// <remarks>
    /// <c>MethodInfo.Invoke</c> on an async method hands back the bare <c>Task</c>; awaiting
    /// it as such observes faults and cancellation, and the <c>Result</c> property is then
    /// safe to read without blocking.
    /// </remarks>
    public static async Task<object?> ResultOf(object? task)
    {
        if (task is not Task t)
        {
            return null;
        }

        await t.ConfigureAwait(false);
        return Get(t, "Result");
    }

    /// <summary>
    /// Finds a public instance method by name and exact parameter list. Returns null rather
    /// than throwing on ambiguity, so a signature change in a future server version reads
    /// as "source unavailable" instead of a crash.
    /// </summary>
    public static MethodInfo? Method(Type type, string name, params Type[] parameters)
    {
        try
        {
            return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, parameters);
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }
}
