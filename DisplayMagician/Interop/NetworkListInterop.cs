using System;
using System.Runtime.InteropServices;

namespace NETWORKLIST
{
    [ComImport]
    [CoClass(typeof(NetworkListManagerClass))]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    public interface NetworkListManager : INetworkListManager
    {
    }

    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    public class NetworkListManagerClass
    {
    }

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface INetworkListManager
    {
        [DispId(6)]
        bool IsConnectedToInternet
        {
            [return: MarshalAs(UnmanagedType.VariantBool)]
            get;
        }
    }
}
