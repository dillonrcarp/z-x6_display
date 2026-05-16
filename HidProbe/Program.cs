using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

Console.WriteLine("=== HWiNFO64.dll Safe Ordinal Probe ===\n");

// Check HWiNFO is running
var hwinfo = Process.GetProcessesByName("HWiNFO64");
Console.WriteLine($"HWiNFO64 running: {hwinfo.Length > 0}\n");

string dllPath = @"C:\Users\dillo\Downloads\GPULCD-extracted\GPU_LCD\msvcr120d.dll";

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern IntPtr LoadLibrary(string lpFileName);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool FreeLibrary(IntPtr hModule);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr lpProcName);

var hModule = LoadLibrary(dllPath);
if (hModule == IntPtr.Zero)
{
    Console.WriteLine($"Failed to load: {Marshal.GetLastWin32Error()}");
    return;
}

int[] validOrdinals = { 1, 2, 3, 4, 5, 6, 7, 8, 20, 21, 22, 23, 24, 25, 26,
    30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44,
    139, 170, 215, 216, 448, 458, 515, 531, 566, 573, 579, 630, 663, 684, 730 };

// The GSCOLER P/Invoke signatures tell us the calling patterns:
// The .NET runtime maps Chinese function names to ordinals via EntryPoint attribute
// Since we can't see the obfuscated IL directly, let's match by signature:
//
// Group 1 - No params, returns int: 礣(), 砲(), 睁() = 3 functions -> ordinals 1-8 range
// Group 2 - (int) returns int: 獽(int), 犌(int) = 2 functions
// Group 3 - (int) returns void: 熛(int) = 1 function
// Group 4 - (int,StringBuilder,int) returns void: 炪(int,sb,int) = 1 function
// Group 5 - (int,int,StringBuilder,int) returns double: 癐,畟,獽,犌,熛,炪 = 6 functions
// Group 6 - (int,int,StringBuilder,int,StringBuilder,int) returns double: 炪 = 1 function
//
// Total: 3+2+1+1+6+1 = 14 P/Invoke functions -> likely ordinals 1-8 + some from 20-44

// Instead of calling functions blindly, let's map the Dotfuscator ordinals
// by examining the .NET assembly's method table more carefully

// Actually, the safest approach: look at the .NET IL to find the EntryPoint ordinals
Console.WriteLine("Extracting P/Invoke entry points from GSCOLER .NET assembly...\n");

FreeLibrary(hModule);

// Use reflection to get the actual DllImport EntryPoint values
var asm = System.Reflection.Assembly.LoadFile(@"C:\Users\dillo\Downloads\GPULCD-extracted\GPU_LCD\GPULCD.exe");

Type[] types;
try { types = asm.GetTypes(); }
catch (System.Reflection.ReflectionTypeLoadException ex)
{
    types = ex.Types.Where(t => t != null).ToArray()!;
}

Console.WriteLine("P/Invoke methods with DLL and EntryPoint:\n");

foreach (var type in types)
{
    try
    {
        var methods = type.GetMethods(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<DllImportAttribute>();
            if (attr != null)
            {
                var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name}"));
                Console.WriteLine($"  DLL: {attr.Value}");
                Console.WriteLine($"  EntryPoint: {attr.EntryPoint ?? "(default=method name)"}");
                Console.WriteLine($"  Method: {method.ReturnType.Name} {method.Name}({parms})");
                Console.WriteLine($"  CallingConvention: {attr.CallingConvention}");
                Console.WriteLine($"  CharSet: {attr.CharSet}");
                Console.WriteLine();
            }
        }
    }
    catch { }
}

Console.WriteLine("=== Done ===");
