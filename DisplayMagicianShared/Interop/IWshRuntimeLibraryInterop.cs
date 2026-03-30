using System;
using System.Runtime.InteropServices;

namespace IWshRuntimeLibrary
{
    [ComImport]
    [CoClass(typeof(WshShellClass))]
    [Guid("F935DC21-1CF0-11D0-ADB9-00C04FD58A0B")]
    public interface WshShell : IWshShell
    {
    }

    [ComImport]
    [Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")]
    public class WshShellClass
    {
    }

    [ComImport]
    [Guid("F935DC21-1CF0-11D0-ADB9-00C04FD58A0B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IWshShell
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object CreateShortcut(string pathLink);
    }

    [ComImport]
    [Guid("F935DC23-1CF0-11D0-ADB9-00C04FD58A0B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IWshShortcut
    {
        [DispId(0)]
        string FullName { get; }

        [DispId(0x3E8)]
        string Arguments { get; set; }

        [DispId(0x3E9)]
        string Description { get; set; }

        [DispId(0x3EA)]
        string Hotkey { get; set; }

        [DispId(0x3EB)]
        string IconLocation { get; set; }

        [DispId(0x3EC)]
        string RelativePath { set; }

        [DispId(0x3ED)]
        string TargetPath { get; set; }

        [DispId(0x3EE)]
        int WindowStyle { get; set; }

        [DispId(0x3EF)]
        string WorkingDirectory { get; set; }

        [DispId(0x7D0)]
        void Save();
    }
}
