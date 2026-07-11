# Login Tab Focus Design

## Goal

Allow keyboard users to move predictably between the server address, user ID, and password fields in the server-entry dialog.

## Behavior

- `Tab` moves focus in this order: server address, user ID, password, then server address again.
- `Shift+Tab` moves through the same fields in reverse order.
- Cancel and start buttons are not included in the focus cycle.
- `Enter` keeps its existing behavior and submits the form from any field.
- The dialog does not force an initial field focus; tabbing begins from the currently focused field, or from the server address when no managed field is focused.

## Implementation

- Assign stable GUI control names immediately before drawing each input field.
- Handle a `KeyDown` event for `KeyCode.Tab` after the fields are registered for the current GUI pass.
- Determine the next control from `GUI.GetNameOfFocusedControl()` and call `GUI.FocusControl(...)`.
- Consume the Tab event so Unity does not also process it.
- Keep the focus-order calculation in a small pure helper so forward, reverse, wraparound, and unknown-focus behavior can be tested without a Unity GUI session.

## Verification

- Unit tests cover forward order, reverse order, wraparound, and starting outside the managed fields.
- The client project builds without warnings or errors.
- The packaged mod is installed locally for an in-game keyboard check.
