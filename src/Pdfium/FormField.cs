using System.Windows;

namespace Fletta.Pdfium;

/// <summary>
/// Ein Formularfeld (Widget) auf der Seite: Nummer in /Annots, Art (FPDF_FORMFIELD_*), Name, Wert, Schalter
/// (FPDF_FORMFLAG_*), Lage in Anteilen der angezeigten Seite (wie PageLink), Auswahlmöglichkeiten bei Listen,
/// gewählte davon (−1 keine), angekreuzt bei Kästchen und Optionsfeldern, Schriftgröße in Punkt (0 = automatisch).
/// </summary>
public sealed record FormField(int Index, int Type, string Name, string Value, int Flags, Rect Area, string[] Options, int Selected,
                               bool Checked, float FontSize)
{
    public bool ReadOnly => (Flags & Native.FormFlagReadOnly) != 0;
    public bool Multiline => (Flags & Native.FormFlagMultiline) != 0;

    /// <summary>Kann Fletta es ausfüllen? Schaltflächen (JavaScript) und Signaturfelder nicht.</summary>
    public bool Fillable => !ReadOnly && Type is Native.FormFieldText or Native.FormFieldCheckBox or Native.FormFieldRadioButton
                                                  or Native.FormFieldComboBox or Native.FormFieldListBox;
}
