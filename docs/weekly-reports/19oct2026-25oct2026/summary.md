Weekly Summary - 19 Oct 2026 to 25 Oct 2026

19 Oct 2026

Tasks
- Phone layout for the app frame (all ND pages): below 720px wide the left navigation is hidden and opens as a slide-out menu from a new menu button in the top bar (closes on tapping outside, on navigating, and on Escape); pages use the full screen width; the top bar fits a 375px phone (logo icon only, New analysis as a + button, shorter workspace switcher and role badge). Tablet and desktop unchanged, including the saved collapsed state and the drag-to-resize width; which menu items each account sees is unchanged (demo accounts included)

- Phone layout for the two side panels on the New analysis and evidence re-check flows (analysis step tracker on the left, admin pipeline progress panel on the right): below 720px each starts as a slim tab at the screen edge and opens as a panel over the page with a dimmed backdrop (closes on tapping outside or Escape), so the page keeps most of the width with no sideways scrolling. Tablet and desktop unchanged, including each panel's collapsed state and dragged width

Bug fixes
- On a phone the navigation stayed fully open beside every page and left the page about 38px wide
- On a phone the step tracker and pipeline panel kept their full widths beside the page and squeezed it

Investigated, follow up pending
- At 768px (tablet) with the navigation, step tracker and pipeline panel all open, the three fixed widths add up to more than the screen and the New analysis page has no room left; collapsing a panel fixes it. Not changed, tablet behaviour was kept as it was
- On a 375px phone the New analysis page clips a few of its own fields by about 20px (section heading, search boxes) with both panel tabs showing; that comes from the page's own layout, not the panels
