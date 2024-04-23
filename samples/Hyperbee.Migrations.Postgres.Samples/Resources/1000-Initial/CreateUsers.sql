-- Create the administration schema
CREATE SCHEMA IF NOT EXISTS administration;

CREATE TABLE IF NOT EXISTS administration.user
(
    user_id      SERIAL PRIMARY KEY,
    name         TEXT,
    email        Text NOT NULL,
    active       BOOLEAN NOT NULL DEFAULT(false),
    created_by   TEXT NOT NULL,
    created_date TIMESTAMP WITH TIME ZONE NOT NULL
);
