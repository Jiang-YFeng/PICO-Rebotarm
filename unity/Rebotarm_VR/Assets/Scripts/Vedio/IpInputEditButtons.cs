using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Keep a separate clear-all action; single-character deletion belongs to the system keyboard.
public sealed class IpInputEditButtons : MonoBehaviour
{
    TMP_InputField input;
    Button clearButton;
    Vector2 originalViewportOffset;
    bool viewportAdjusted;
    Coroutine caretRoutine;

    public void Initialize(TMP_InputField field, Button template)
    {
        if (clearButton) clearButton.onClick.RemoveListener(ClearAll);
        if (input) input.onSelect.RemoveListener(OnInputSelected);

        input = field;
        input.onFocusSelectAll = false;
        input.resetOnDeActivation = false;
        // Keep buttons within the existing input rectangle, reserving the right edge.
        if (input.textViewport && !viewportAdjusted)
        {
            originalViewportOffset = input.textViewport.offsetMax;
            input.textViewport.offsetMax = new Vector2(originalViewportOffset.x - 85f, originalViewportOffset.y);
            viewportAdjusted = true;
        }
        clearButton = input.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "ClearIpButton");
        if (clearButton) clearButton.onClick.AddListener(ClearAll);
        input.onSelect.AddListener(OnInputSelected);
    }

    public void ClearAll()
    {
        if (!input) return;
        input.text = string.Empty;
        input.ActivateInputField();
        MoveCaretAfterKeyboardUpdate(0);
    }

    void OnInputSelected(string _)
    {
        // XR selection does not provide a reliable character hit position. Put
        // the insertion point at the end so Backspace deletes one IP character
        // at a time instead of starting before the first character.
        MoveCaretAfterKeyboardUpdate(input.text.Length);
    }

    void MoveCaretAfterKeyboardUpdate(int position)
    {
        if (caretRoutine != null) StopCoroutine(caretRoutine);
        caretRoutine = StartCoroutine(SetCaretNextFrame(position));
    }

    IEnumerator SetCaretNextFrame(int position)
    {
        // TMP and Android's soft keyboard both update selection after focus, so
        // wait for them before applying the final caret position.
        yield return null;
        yield return null;
        if (!input) yield break;
        int clamped = Mathf.Clamp(position, 0, input.text.Length);
        input.stringPosition = clamped;
        input.caretPosition = clamped;
        input.selectionAnchorPosition = clamped;
        input.selectionFocusPosition = clamped;
        input.ForceLabelUpdate();
        caretRoutine = null;
    }

    void OnDestroy()
    {
        if (input)
        {
            input.onSelect.RemoveListener(OnInputSelected);
            if (input.textViewport && viewportAdjusted) input.textViewport.offsetMax = originalViewportOffset;
        }
        if (clearButton) clearButton.onClick.RemoveListener(ClearAll);
    }
}
