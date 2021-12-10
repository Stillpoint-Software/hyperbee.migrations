# Notes

## TODO

CREATE SCOPE `Hyperbee`.migrations
CREATE COLLECTION `Hyperbee`.`migrations`.`ledger`;
CREATE PRIMARY INDEX ON `default`:`Hyperbee`.`migrations`.`ledger`;
SELECT * FROM `Hyperbee`.migrations.ledger;


Check for bucket
SELECT * FROM system:buckets WHERE name = "Hyperbee"

Check for scope
SELECT * FROM system:scopes WHERE `bucket` = "Hyperbee" AND name = "migrations"

Check for collection
SELECT * FROM system:keyspaces WHERE `bucket` = "Hyperbee" AND `scope` = "migrations" AND name = "ledger"

Check for primary index
SELECT * FROM system:indexes WHERE bucket_id = "Hyperbee" AND scope_id = "migrations" AND keyspace_id = "ledger" AND is_primary