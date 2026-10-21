using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CaseKeeper.Services;

/// <summary>
/// Opens the standard Windows Share sheet (email, Nearby Sharing, OneDrive, Teams, etc.) for desktop windows.
/// </summary>
public static class ShareService
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private static readonly HashSet<IntPtr> RegisteredWindows = new();
    private static readonly Dictionary<IntPtr, (string Title, IReadOnlyList<IStorageItem> Items)> Pending = new();

    public static async Task ShareFilesAsync(Window owner, string title, IEnumerable<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths.Where(File.Exists))
            items.Add(await StorageFile.GetFileFromPathAsync(p));
        if (items.Count == 0) throw new FileNotFoundException("Nothing to share.");

        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;

        if (RegisteredWindows.Add(hwnd))
        {
            var ptr = interop.GetForWindow(hwnd, ref iid);
            var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);
            manager.DataRequested += (_, args) =>
            {
                if (!Pending.TryGetValue(hwnd, out var data)) return;
                args.Request.Data.Properties.Title = data.Title;
                args.Request.Data.Properties.Description = data.Items.Count == 1 ? data.Items[0].Name : $"{data.Items.Count} files";
                args.Request.Data.SetStorageItems(data.Items);
            };
        }

        Pending[hwnd] = (title, items);
        interop.ShowShareUIForWindow(hwnd);
    }
}
