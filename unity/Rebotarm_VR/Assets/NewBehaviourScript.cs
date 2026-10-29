using UnityEngine;
using UnityEngine.InputSystem;

public class ControllerButtonTest : MonoBehaviour
{
    public InputActionProperty trigger;
    public InputActionProperty grip;

    private Material testMaterial;

    void Start()
    {
        testMaterial = GetComponent<Renderer>().material;
    }

    void Update()
    {
        bool triggerPressed =
            trigger.action != null && trigger.action.IsPressed();

        bool gripPressed =
            grip.action != null && grip.action.IsPressed();

        Color color = triggerPressed && gripPressed ? Color.yellow :
                      triggerPressed ? Color.red :
                      gripPressed ? Color.green : Color.white;

        if (testMaterial.HasProperty("_BaseColor"))
            testMaterial.SetColor("_BaseColor", color);
        else
            testMaterial.color = color;
    }

    void OnDestroy()
    {
        if (testMaterial != null)
            Destroy(testMaterial);
    }
}