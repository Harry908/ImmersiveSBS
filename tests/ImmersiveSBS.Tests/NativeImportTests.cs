using System.Reflection;
using System.Runtime.InteropServices;

namespace ImmersiveSBS.Tests;

/// <summary>
/// Every native function the app imports exists in the DLL it names. A wrong DLL only fails
/// when the function is first called: GetDpiForMonitor pointed at user32.dll (it is in
/// shcore.dll) once stopped the app at start.
/// </summary>
public class NativeImportTests
{
    [Fact]
    public void EveryImportResolves()
    {
        var missing = new List<string>();
        int checkedCount = 0;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (Type type in typeof(AppController).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(all))
            {
                (string Library, string Entry)? import = Import(method);
                if (import is not var (library, entry)) continue;
                checkedCount++;
                if (!NativeLibrary.TryLoad(library, out IntPtr handle))
                {
                    missing.Add($"{type.Name}.{method.Name}: {library} does not load");
                    continue;
                }
                if (!NativeLibrary.TryGetExport(handle, entry, out _) && !NativeLibrary.TryGetExport(handle, entry + "W", out _))
                    missing.Add($"{type.Name}.{method.Name}: {entry} is not in {library}");
            }
        }

        Assert.True(checkedCount > 20, $"only {checkedCount} imports found");
        Assert.Empty(missing);
    }

    private static (string Library, string Entry)? Import(MethodInfo method)
    {
        if (method.GetCustomAttribute<LibraryImportAttribute>() is { } li)
            return (li.LibraryName, li.EntryPoint ?? method.Name);
        if (method.GetCustomAttribute<DllImportAttribute>() is { } di)
            return (di.Value, di.EntryPoint ?? method.Name);
        return null;
    }
}
