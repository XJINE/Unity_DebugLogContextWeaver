using UnityEngine;

public class Sample : MonoBehaviour
{
    private void Start()
    {
        Debug.Log       ("Log");                 // Log       (object)           → Log       (object, Object)
        Debug.LogWarning("LogWarning");          // LogWarning(object)           → LogWarning(object, Object)
        Debug.LogError  ("LogError");            // LogError  (object)           → LogError  (object, Object)
        Debug.LogFormat ("LogFormat {0}", name); // LogFormat (string, object[]) → LogFormat (Object, string, object[])
        Debug.Assert    (false, "Assert");       // Assert    (bool, string)     → Assert    (bool, string, Object)

        LogFromAnotherMethod();
    }

    private void LogFromAnotherMethod()
    {
        Debug.Log("Log from another instance method");
    }
}