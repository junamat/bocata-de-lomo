using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RefereeHubApi.Secrets
{
    // uses the *v_sync variants of the libsecret calls so there's no varargs P/Invoke.
    [SupportedOSPlatform("linux")]
    public sealed class LibsecretSecretStore : ISecretStore, IDisposable
    {
        public const string DEFAULT_SCHEMA_NAME = "RefereeHubApi.Secret";

        private const string attribute_name = "key";
        private const string libsecret = "libsecret-1.so.0";
        private const string libglib = "libglib-2.0.so.0";

        // struct SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; gint reserved; gpointer reserved1..7; }
        // struct SecretSchemaAttribute { const gchar *name; SecretSchemaAttributeType type; }
        private const int schema_size = 8 + 8 + 32 * 16 + 8 + 7 * 8;

        private readonly IntPtr schema;
        private readonly IntPtr schemaName;
        private readonly IntPtr attributeName;
        private readonly string label;
        private bool disposed;

        public LibsecretSecretStore(string schemaName = DEFAULT_SCHEMA_NAME, string label = "RefereeHubApi")
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("libsecret is Linux only.");

            this.label = label;
            this.schemaName = Marshal.StringToCoTaskMemUTF8(schemaName);
            attributeName = Marshal.StringToCoTaskMemUTF8(attribute_name);

            schema = Marshal.AllocHGlobal(schema_size);
            unsafe
            {
                new Span<byte>((void*)schema, schema_size).Clear();
            }

            Marshal.WriteIntPtr(schema, 0, this.schemaName); // name
            Marshal.WriteInt32(schema, 8, 0); // SECRET_SCHEMA_NONE
            Marshal.WriteIntPtr(schema, 16, attributeName); // attributes[0].name
            Marshal.WriteInt32(schema, 24, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
            // attributes[1] stays zeroed: list terminator.
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.Run(() => withAttributes(key, (attrs) =>
            {
                IntPtr result = secret_password_lookupv_sync(schema, attrs, IntPtr.Zero, out IntPtr error);
                throwIfError(error, "lookup");

                if (result == IntPtr.Zero)
                    return null;

                try
                {
                    return Marshal.PtrToStringUTF8(result);
                }
                finally
                {
                    secret_password_free(result);
                }
            }), cancellationToken);

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.Run(() => withAttributes<object?>(key, attrs =>
            {
                IntPtr collection = Marshal.StringToCoTaskMemUTF8("default");
                IntPtr itemLabel = Marshal.StringToCoTaskMemUTF8($"{label}: {key}");
                IntPtr password = Marshal.StringToCoTaskMemUTF8(value);

                try
                {
                    bool ok = secret_password_storev_sync(schema, attrs, collection, itemLabel, password, IntPtr.Zero, out IntPtr error);
                    throwIfError(error, "store");

                    if (!ok)
                        throw new SecretStoreException("libsecret store failed without an error.");

                    return null;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(collection);
                    Marshal.FreeCoTaskMem(itemLabel);
                    zeroAndFree(password, value);
                }
            }), cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.Run(() => withAttributes<object?>(key, attrs =>
            {
                secret_password_clearv_sync(schema, attrs, IntPtr.Zero, out IntPtr error);
                throwIfError(error, "clear");
                return null;
            }), cancellationToken);

        private T withAttributes<T>(string key, Func<IntPtr, T> action)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            IntPtr keyPtr = Marshal.StringToCoTaskMemUTF8(key);
            IntPtr table = g_hash_table_new(str_hash, str_equal);

            try
            {
                g_hash_table_insert(table, attributeName, keyPtr);
                return action(table);
            }
            finally
            {
                g_hash_table_unref(table);
                Marshal.FreeCoTaskMem(keyPtr);
            }
        }

        private static void throwIfError(IntPtr error, string operation)
        {
            if (error == IntPtr.Zero)
                return;

            // struct GError { GQuark domain; gint code; gchar *message; }
            int code = Marshal.ReadInt32(error, 4);
            string? message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
            g_error_free(error);
            throw new SecretStoreException($"libsecret {operation} failed ({code}): {message}");
        }

        private static void zeroAndFree(IntPtr ptr, string original)
        {
            int length = System.Text.Encoding.UTF8.GetByteCount(original);
            unsafe
            {
                new Span<byte>((void*)ptr, length).Clear();
            }

            Marshal.FreeCoTaskMem(ptr);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Marshal.FreeHGlobal(schema);
            Marshal.FreeCoTaskMem(schemaName);
            Marshal.FreeCoTaskMem(attributeName);
        }

        private static readonly IntPtr glib_handle = NativeLibrary.Load(libglib);
        private static readonly IntPtr str_hash = NativeLibrary.GetExport(glib_handle, "g_str_hash");
        private static readonly IntPtr str_equal = NativeLibrary.GetExport(glib_handle, "g_str_equal");

        [DllImport(libsecret)]
        [return: MarshalAs(UnmanagedType.Bool)] // gboolean = 4-byte int
        private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

        [DllImport(libsecret)]
        private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(libsecret)]
        [return: MarshalAs(UnmanagedType.Bool)] // gboolean = 4-byte int
        private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(libsecret)]
        private static extern void secret_password_free(IntPtr password);

        [DllImport(libglib)]
        private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

        [DllImport(libglib)]
        [return: MarshalAs(UnmanagedType.Bool)] // gboolean = 4-byte int
        private static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [DllImport(libglib)]
        private static extern void g_hash_table_unref(IntPtr table);

        [DllImport(libglib)]
        private static extern void g_error_free(IntPtr error);
    }

    public class SecretStoreException : Exception
    {
        public SecretStoreException(string message)
            : base(message)
        {
        }
    }
}
