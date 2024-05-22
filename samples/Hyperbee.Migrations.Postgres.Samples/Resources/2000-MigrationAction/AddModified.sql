-- Add modified to administration table
ALTER TABLE administration.user ADD COLUMN IF NOT EXISTS modified_by TEXT;
ALTER TABLE administration.user ADD COLUMN IF NOT EXISTS modified_date TIMESTAMP WITH TIME ZONE;