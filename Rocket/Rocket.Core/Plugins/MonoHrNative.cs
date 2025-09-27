using System;
using System.Runtime.InteropServices;

namespace Rocket.Core.Plugins;

public class MonoHrNative
{
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mono_hr_create_domain(string name);

    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mono_hr_unload_domain(IntPtr handle);
}