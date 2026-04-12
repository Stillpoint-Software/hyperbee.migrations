-- Create the sample schema and tables
CREATE SCHEMA IF NOT EXISTS sample;

CREATE TABLE IF NOT EXISTS sample.users
(
    user_id      SERIAL PRIMARY KEY,
    name         TEXT,
    email        TEXT NOT NULL,
    active       BOOLEAN NOT NULL DEFAULT false,
    role         TEXT,
    created_date TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE TABLE IF NOT EXISTS sample.products
(
    product_id   SERIAL PRIMARY KEY,
    name         TEXT,
    category     TEXT,
    price        NUMERIC(10,2),
    active       BOOLEAN NOT NULL DEFAULT false,
    created_date TIMESTAMP WITH TIME ZONE NOT NULL
);
