
- Fixed: inserts and upserts with explicit identity values failed (error 544). They now run between `SET IDENTITY_INSERT ON/OFF`, and the setting is switched off again when the insert fails, so pooled sessions are not left with it on.
