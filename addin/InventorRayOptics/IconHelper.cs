using System.Drawing;
using System.Windows.Forms;

namespace InventorRayOptics
{
    /// <summary>
    /// Converts .NET images to the OLE IPictureDisp COM objects that Inventor's
    /// ButtonDefinition icon parameters require. AddButtonDefinition's StandardIcon/
    /// LargeIcon parameters are typed as plain `object` in the interop, so the raw
    /// IPictureDisp wrapper can be passed through without a separate stdole reference.
    /// </summary>
    internal static class IconHelper
    {
        private sealed class PictureConverter : AxHost
        {
            private PictureConverter() : base("") { }

            public static object FromImage(Image image) => GetIPictureDispFromPicture(image);
        }

        public static object LoadPictureDisp(string pngPath)
        {
            using (var image = Image.FromFile(pngPath))
            {
                return PictureConverter.FromImage(image);
            }
        }
    }
}
