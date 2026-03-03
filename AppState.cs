namespace FbmodDecompiler
{
    internal static class AppState
    {
        public static readonly AudioService Audio = new AudioService();

        public static string GetAppVersion()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? v.ToString() : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
