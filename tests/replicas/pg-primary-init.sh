#!/bin/bash
# Lets the replica stream WAL from the primary (throwaway test containers: trust auth).
echo "host replication all all trust" >> "$PGDATA/pg_hba.conf"
