# Add Tests

Write comprehensive tests for the DataGrid component:

1. **Unit tests** (Vitest + React Testing Library):
   - Renders correct number of rows
   - Renders column headers
   - Sorting toggles on header click
   - Filter narrows displayed rows
   - onRowClick fires with correct data
   - Empty state renders properly

2. **Type tests**:
   - Verify generic type inference works
   - Column definitions match data shape

3. Ensure all tests pass with `npm test`
