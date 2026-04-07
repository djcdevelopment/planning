# Build Grid Component

Build a reusable DataGrid component with the following features:

1. **Props interface**: Generic `DataGrid<T>` that accepts:
   - `data: T[]` — array of row data
   - `columns: ColumnDef<T>[]` — column definitions
   - `onRowClick?: (row: T) => void`
   - `sortable?: boolean`
   - `filterable?: boolean`

2. **Core features**:
   - Column sorting (click header to toggle asc/desc)
   - Text filter per column
   - Responsive layout
   - Striped rows with hover highlight

3. **Implementation**:
   - Use `@tanstack/react-table` for table logic
   - CSS modules for styling
   - Proper TypeScript generics throughout
