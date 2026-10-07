using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// The slot picker in the tool header: which connected slot the slot tools show, and a way to switch (the slot cards in
/// the SLOTS panel are the other). Ctrl+Tab and Ctrl+Shift+Tab go through the connected slots the same way.
/// </summary>
public partial class MainTrackerWindow
{
    private OptionButton? _slotPicker;
    private readonly List<SlotTrackerControl> _pickerSlots = new();

    /// <summary>The picker, for the tool header. Hidden while a tool that isn't per slot shows, or no slot is connected.</summary>
    private OptionButton BuildSlotPicker()
    {
        _slotPicker = new OptionButton { Visible = false, TooltipText = Tr("The slot the slot tools show (Ctrl+Tab: the next one)"), AccessibilityName = Tr("Slot shown") };
        _slotPicker.ItemSelected += index =>
        {
            if (index >= 0 && index < _pickerSlots.Count) ((AP_Atlas.UI.IPropertiesHost)this).SelectSlot(_pickerSlots[(int)index]);
        };
        return _slotPicker;
    }

    /// <summary>Lists the connected slots in the picker, the selected one chosen. Runs whenever the slots or the selection change.</summary>
    private void RefreshSlotPicker()
    {
        if (_slotPicker == null) return;
        var slots = ActiveSlotNodes().OfType<SlotTrackerControl>().ToList();
        if (slots.Count != _pickerSlots.Count || slots.Where((slot, i) => _pickerSlots[i] != slot).Any())
        {
            _pickerSlots.Clear();
            _pickerSlots.AddRange(slots);
            _slotPicker.Clear();
            foreach (var slot in slots)
            {
                string multiworld = _profiles.FirstOrDefault(p => p.Id == slot.ProfileId)?.Name ?? "";
                _slotPicker.AddItem(multiworld.Length > 0 ? $"{slot.SlotName}  ·  {multiworld}" : slot.SlotName);
            }
        }
        int selected = _currentSelectedSlot == null ? -1 : _pickerSlots.IndexOf(_currentSelectedSlot);
        if (_slotPicker.Selected != selected) _slotPicker.Select(selected);
        _slotPicker.Visible = _currentTool.Scope == AP_Atlas.UI.ToolScope.Slot && slots.Count > 0;
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous connected slot, around the end, as a slot card would select it.</summary>
    private void CycleSlot(int delta)
    {
        var slots = ActiveSlotNodes().OfType<SlotTrackerControl>().ToList();
        if (slots.Count == 0) return;
        int index = _currentSelectedSlot == null ? -1 : slots.IndexOf(_currentSelectedSlot);
        int next = index < 0 ? (delta > 0 ? 0 : slots.Count - 1) : ((index + delta) % slots.Count + slots.Count) % slots.Count;
        ((AP_Atlas.UI.IPropertiesHost)this).SelectSlot(slots[next]);
    }
}
