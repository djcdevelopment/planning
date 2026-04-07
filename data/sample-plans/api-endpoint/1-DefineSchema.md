# Define API Schema

Define the data models and API schema for a user management endpoint:

1. **User model**:
   - id: UUID
   - email: string (unique)
   - displayName: string
   - role: "admin" | "user" | "viewer"
   - createdAt: ISO datetime
   - updatedAt: ISO datetime

2. **API endpoints** to define:
   - `GET /api/users` — list users (paginated)
   - `GET /api/users/:id` — get single user
   - `POST /api/users` — create user
   - `PUT /api/users/:id` — update user
   - `DELETE /api/users/:id` — delete user

3. Create TypeScript types/interfaces for all models and API request/response shapes
