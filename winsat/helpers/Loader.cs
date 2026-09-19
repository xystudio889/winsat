using Windows.ApplicationModel.Resources;

namespace winsat.helpers
{
    class Loader
    {
        private static ResourceLoader loader = ResourceLoader.GetForViewIndependentUse("Resources");

        public static string GetString(string str)
        {
            return loader.GetString(str);
        }
    }
}
