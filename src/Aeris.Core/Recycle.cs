using System.IO;
using System.Runtime.InteropServices;

namespace Aeris;

public static class Recycle
{
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    public static void Send(LargeFile file, IReadOnlyCollection<string> exclusions, IntPtr owner)
    {
        if (!Analysis.Unchanged(file) || Storage.IsExcluded(file.Path, exclusions)) throw new ChangedException(Loc.Get("Error.FileChanged"));
        Shell(file.Path, owner);
        if (File.Exists(file.Path)) throw new IOException(Loc.Get("Error.NotRecycled"));
    }

    /// <summary>Recycles a folder that is still empty apart from the junk files the empty-folder search ignores.</summary>
    public static void SendFolder(string path, bool ignoreJunk, IReadOnlyCollection<string> exclusions, IntPtr owner)
    {
        if (!Directory.Exists(path) || !Storage.SafePath(path) || Storage.IsExcluded(path, exclusions) || !EmptyFolders.IsEmpty(path, ignoreJunk))
            throw new ChangedException(Loc.Get("Error.FolderChanged"));
        Shell(path, owner);
        if (Directory.Exists(path)) throw new IOException(Loc.Get("Error.NotRecycled"));
    }

    private static void Shell(string path, IntPtr owner)
    {
        if (!OperatingSystem.IsWindows())
        {
            var info = new System.Diagnostics.ProcessStartInfo("gio") { UseShellExecute = false, RedirectStandardError = true };
            info.ArgumentList.Add("trash"); info.ArgumentList.Add("--"); info.ArgumentList.Add(path);
            try
            {
                using var process = System.Diagnostics.Process.Start(info)!;
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException(error.Trim());
            }
            catch (System.ComponentModel.Win32Exception e) { throw new IOException(Loc.Get("Platform.TrashUnavailable"), e); }
            return;
        }
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) throw new IOException(Loc.Get("Error.LocalOnly"));
        IShellItem? item = null;
        IFileOperation? operation = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x20000000 | 0x00000400 | 0x00000004 | 0x00000010);
            operation.DeleteItem(item, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted) throw new IOException(Loc.Get("Error.NotRecycled"));
        }
        finally
        {
            if (item != null) Marshal.FinalReleaseComObject(item);
            if (operation != null) Marshal.FinalReleaseComObject(operation);
        }
    }

    /// <summary>
    /// Moves the plan's included files and folders to the Recycle Bin, one by one. Must run on an STA thread.
    /// Duplicate copies are hashed again together with the copy that stays.
    /// </summary>
    public static CleanupResult Execute(CleanupPlan plan, IReadOnlyCollection<string> exclusions, IntPtr owner, Journal journal, CancellationToken token, IProgress<int>? progress = null, bool ignoreJunk = true)
    {
        if (!Duplicates.KeepsOne(plan)) throw new InvalidOperationException(Loc.Get("Recycle.KeepOne"));
        var result = new CleanupResult { Id = journal.Id };
        int index = 0;
        foreach (var item in plan.Included.ToList())
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(++index);
            string outcome = Outcome.Recycled;
            string? error = null;
            try
            {
                if (item.IsFolder) SendFolder(item.Path, ignoreJunk, exclusions, owner);
                else
                {
                    Duplicates.Verify(item);
                    Send(new LargeFile(item.Path, item.Size, item.Modified, item.Created), exclusions, owner);
                }
            }
            catch (ChangedException e) { outcome = Outcome.Changed; error = e.Message; }
            catch (IOException e) when (Cleanup.IsLocked(e)) { outcome = Outcome.InUse; error = e.Message; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException) { outcome = Outcome.Failed; error = e.Message; }
            result.Count(item, outcome, error);
            journal.Write(item, outcome, null, error);
        }
        return result;
    }

    /// <summary>Opens the Recycle Bin in File Explorer.</summary>
    public static void Open() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
}

public class ChangedException(string message) : IOException(message);
