using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Rocket.Core.Plugins;

public static class Icalls
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern Assembly mono_hr_load_plugin(IntPtr domainHandle, byte[] data);
}