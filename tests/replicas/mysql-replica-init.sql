-- Runs once when the replica initializes: replicate everything from the primary by GTID position.
-- The root password is empty on both servers (throwaway test containers).
CHANGE REPLICATION SOURCE TO SOURCE_HOST = 'mysql-primary', SOURCE_USER = 'root', SOURCE_PASSWORD = '',
    SOURCE_AUTO_POSITION = 1, GET_SOURCE_PUBLIC_KEY = 1;
START REPLICA;
-- Read-only from here on (a startup flag would block the image's own init steps).
SET PERSIST super_read_only = ON;
