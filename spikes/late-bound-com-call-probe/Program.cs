using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CSharp.RuntimeBinder;
using RuntimeBinder = Microsoft.CSharp.RuntimeBinder.Binder;

internal static class Program
{
    private const string ProgId = "Illustrator.Application";

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(
        string lpszProgID,
        out Guid lpclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [STAThread]
    private static int Main()
    {
        object? appObject = null;
        object? documentObject = null;
        object? artboardsObject = null;

        try
        {
            Marshal.ThrowExceptionForHR(
                CLSIDFromProgID(ProgId, out var clsid));
            Marshal.ThrowExceptionForHR(
                GetActiveObject(ref clsid, IntPtr.Zero, out appObject));

            dynamic app = appObject;

            if (Convert.ToInt32(app.Documents.Count) == 0)
            {
                Console.WriteLine(
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        skipped = true,
                        reason = "No active document."
                    }));
                return 2;
            }

            documentObject = app.ActiveDocument;
            dynamic document = documentObject;
            artboardsObject = document.Artboards;
            dynamic artboards = artboardsObject;

            var direct = Capture(
                "dynamic",
                () => artboards.GetActiveArtboardIndex());

            var reflection = Capture(
                "reflection",
                () => artboardsObject
                    .GetType()
                    .InvokeMember(
                        "GetActiveArtboardIndex",
                        BindingFlags.InvokeMethod |
                        BindingFlags.Public |
                        BindingFlags.Instance |
                        BindingFlags.OptionalParamBinding,
                        binder: null,
                        target: artboardsObject,
                        args: Array.Empty<object>()));

            var csharpBinder = Capture(
                "csharp-binder",
                () => BuildInvoker("GetActiveArtboardIndex")(
                    artboardsObject,
                    Array.Empty<object?>()));

            Console.WriteLine(
                JsonSerializer.Serialize(new
                {
                    ok = direct.Ok &&
                         reflection.Ok &&
                         csharpBinder.Ok,
                    direct,
                    reflection,
                    csharpBinder
                }));

            return 0;
        }
        finally
        {
            Release(artboardsObject);
            Release(documentObject);
            Release(appObject);
        }
    }

    private static Func<object, object?[], object?> BuildInvoker(
        string member)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var args = Expression.Parameter(typeof(object[]), "args");

        var binder = RuntimeBinder.InvokeMember(
            CSharpBinderFlags.None,
            member,
            typeArguments: null,
            typeof(Program),
            [
                CSharpArgumentInfo.Create(
                    CSharpArgumentInfoFlags.None,
                    name: null)
            ]);

        var body = Expression.Dynamic(
            binder,
            typeof(object),
            target);

        return Expression
            .Lambda<Func<object, object?[], object?>>(
                body,
                target,
                args)
            .Compile();
    }

    private static ProbeResult Capture(
        string mechanism,
        Func<object?> action)
    {
        try
        {
            return new ProbeResult(
                mechanism,
                true,
                Convert.ToString(action()),
                null,
                null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(
                mechanism,
                false,
                null,
                ex.Message,
                $"0x{unchecked((uint)ex.HResult):X8}");
        }
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }

    private sealed record ProbeResult(
        string Mechanism,
        bool Ok,
        string? Value,
        string? Error,
        string? HResultHex);
}
