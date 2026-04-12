-- Create secondary indexes on users and products tables
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email ON sample.users (email);
CREATE INDEX IF NOT EXISTS ix_users_active ON sample.users (active);
CREATE INDEX IF NOT EXISTS ix_users_role ON sample.users (role);
CREATE INDEX IF NOT EXISTS ix_products_category ON sample.products (category);
CREATE INDEX IF NOT EXISTS ix_products_price ON sample.products (price);
