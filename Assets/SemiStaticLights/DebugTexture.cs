using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public static class DebugTexture
{
    static Texture2D CopyTexture(RenderTexture source)
    {
        var tex = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);

        var saved = RenderTexture.active;
        RenderTexture.active = source;
        tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        tex.Apply();
        RenderTexture.active = saved;

        return tex;
    }

    public static bool WriteToPNGFile(this Texture2D source, string filename)
    {
        try
        {
            byte[] rawdata = ImageConversion.EncodeToPNG(source);
            System.IO.File.WriteAllBytes(filename, rawdata);
            Debug.Log("Wrote " + filename);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning(e);
            return false;
        }
    }

    public static bool WriteToPNGFile(this RenderTexture source, string filename)
    {
        try
        {
            Texture2D tex = CopyTexture(source);
            try
            {
                return tex.WriteToPNGFile(filename);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning(e);
            return false;
        }
    }
}
