using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Compositor.App.Ai;

internal interface IAiCredentialStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

// Account token bundles can be larger than a generic Credential Manager entry. DPAPI encrypts the
// complete bundle for the current Windows user without adding a library or splitting rotating tokens.
internal sealed class AiCredentialStore : IAiCredentialStore
{
    private readonly string directory;
    private const int MaxBytes = 1024 * 1024;

    public AiCredentialStore(string? directory = null)
    {
        string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor");
        this.directory = Path.GetFullPath(directory ?? Path.Combine(root, "ai"));
    }

    public string? Read(string name)
    {
        string path = FilePath(name);
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        if (file.Length > MaxBytes) throw new AiServiceException("The saved AI credentials exceed the local size limit.");
        byte[] encrypted = new byte[checked((int)file.Length)];
        file.ReadExactly(encrypted);
        byte[] plain = Transform(encrypted, protect: false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Write(string name, string value)
    {
        string path = FilePath(name);
        if (Encoding.UTF8.GetByteCount(value) > MaxBytes / 2) throw new ArgumentException("The AI credentials are too large.");
        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] encrypted;
        try { encrypted = Transform(plain, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(string name) => File.Delete(FilePath(name));

    private string FilePath(string name) => name is "api-key" or "chatgpt-account"
        ? Path.Combine(directory, name + ".dat") : throw new ArgumentException("Unknown AI credential name.");

    private static byte[] Transform(byte[] input, bool protect)
    {
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var source = new DataBlob { Size = input.Length, Data = pin.AddrOfPinnedObject() };
        DataBlob output = default;
        try
        {
            bool ok = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new AiServiceException(protect
                ? "Windows could not protect the AI credentials." : "Windows could not unlock the saved AI credentials. Sign in again.");
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            pin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
