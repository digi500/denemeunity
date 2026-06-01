using UnityEngine;

public class VersionDisplay : MonoBehaviour
{
    void Start()
    {
        // Try to find TextMeshProUGUI component via Reflection to prevent TMPro reference issues
        System.Type tmproType = System.Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
        if (tmproType == null)
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                tmproType = assembly.GetType("TMPro.TextMeshProUGUI");
                if (tmproType != null) break;
            }
        }

        if (tmproType != null)
        {
            var textComponent = GetComponent(tmproType);
            if (textComponent != null)
            {
                TextAsset versionAsset = Resources.Load<TextAsset>("version");
                string versionStr = "1.0.0";
                if (versionAsset != null)
                {
                    versionStr = versionAsset.text.Trim();
                }
                
                var textProp = tmproType.GetProperty("text");
                if (textProp != null)
                {
                    textProp.SetValue(textComponent, "v" + versionStr);
                }
            }
        }
    }
}
