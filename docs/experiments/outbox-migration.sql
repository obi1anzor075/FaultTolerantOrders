CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012100116_InitialOrdersWithOutbox') THEN
    CREATE TABLE IF NOT EXISTS "Orders" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "Total" numeric(18,2) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Orders" PRIMARY KEY ("Id")
    );
    CREATE TABLE IF NOT EXISTS "OrderItems" (
        "Id" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "ProductId" uuid NOT NULL,
        "Quantity" integer NOT NULL,
        "UnitPrice" numeric(18,2) NOT NULL,
        CONSTRAINT "PK_OrderItems" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId")
            REFERENCES "Orders" ("Id") ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012100116_InitialOrdersWithOutbox') THEN
    CREATE TABLE "OutboxMessages" (
        "Id" uuid NOT NULL,
        "OrderId" uuid NOT NULL,
        "Type" character varying(256) NOT NULL,
        "Payload" jsonb NOT NULL,
        "OccurredAt" timestamp with time zone NOT NULL,
        "PublishedAt" timestamp with time zone,
        "RetryCount" integer NOT NULL,
        "NextRetryAt" timestamp with time zone,
        "Error" character varying(2000),
        CONSTRAINT "PK_OutboxMessages" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OutboxMessages_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012100116_InitialOrdersWithOutbox') THEN
    CREATE INDEX "IX_OutboxMessages_NextRetryAt_OccurredAt" ON "OutboxMessages" ("NextRetryAt", "OccurredAt") WHERE "PublishedAt" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012100116_InitialOrdersWithOutbox') THEN
    CREATE UNIQUE INDEX "IX_OutboxMessages_OrderId_Type" ON "OutboxMessages" ("OrderId", "Type");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012100116_InitialOrdersWithOutbox') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261012100116_InitialOrdersWithOutbox', '10.0.9');
    END IF;
END $EF$;
COMMIT;

