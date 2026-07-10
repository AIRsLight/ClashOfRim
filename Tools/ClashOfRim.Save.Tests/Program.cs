using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.ThirdPartyCompat.ServerPlugin;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

var tests = new (string Name, Action Run)[]
{
    ("服务器数据库新建时写入当前 schema 版本", VerifyServerDatabaseMigrationInitializesNewDatabase),
    ("服务器数据库旧版本按匹配步骤迁移并保留其他数据", VerifyServerDatabaseMigrationUpgradesLegacyDatabase),
    ("服务器数据库从管理员快照恢复世界底图", VerifyServerDatabaseMigrationRecoversWorldSubstrate),
    ("服务器数据库未来 schema 版本拒绝启动", VerifyServerDatabaseMigrationRejectsFutureVersion),
    ("世界底图包仅保留静态世界结构并可压缩往返", VerifyWorldSubstratePackageRoundTrip),
    ("世界底图包拒绝缺失网格的存档", VerifyWorldSubstratePackageRejectsMissingGrid),
    ("世界底图包持久化后才开放世界会话", VerifyWorldSubstrateRegistryReadiness),
    ("压缩财富历史可作为榜单财富兜底", VerifyDeflatedWealthHistoryFallback),
    ("玩家殖民者人数索引只统计玩家类人 pawn", VerifyPlayerColonistCountIndex),
    ("快照封装保留身份版本和稳定哈希", VerifySnapshotPackageEnvelope),
    ("快照上传通过校验后进入最新索引", VerifySnapshotUploadAccepted),
    ("快照文件存储可在重启后恢复最新索引", VerifyFileSnapshotStorePersistence),
    ("新版单文件快照包可直接解析", VerifySnapshotPackageFileReader),
    ("快照上传拒绝身份哈希和版本异常", VerifySnapshotUploadRejections),
    ("袭击结算证据必须携带指定事件的战场地图", VerifyRaidSettlementEvidenceUploadRequiresEventMap),
    ("只读观察加载绑定快照地图和权限边界", VerifyReadOnlyObservationBoundary),
    ("袭击副本陷阱按批准清单隐藏和揭示", VerifyRaidTrapVisibility),
    ("陷阱类型上传清单只批准继承项和管理员确认项", VerifyTrapClassificationUploadManifest),
    ("指定地图生成客户端可用的具体隐藏对象清单", VerifyRaidHiddenTrapList),
    ("具体隐藏对象清单可转换为客户端下发包", VerifyRaidHiddenTrapDelivery),
    ("袭击返回快照绑定事件快照和目标地图后结算", VerifyRaidSettlementReturnProcessing),
    ("袭击普通攻击证据快照可映射本地战场地图结算", VerifyRaidSettlementReturnProcessingWithAttackerEvidenceMap),
    ("袭击返回快照缺少原始对象标记时使用稳定对象键", VerifyRaidSettlementMatchesStableThingKeysWithoutOriginalThingId),
    ("袭击返回快照聚合重复原始对象映射", VerifyRaidSettlementAggregatesDuplicateReturnedOriginalThingId),
    ("袭击结算忽略 pawn 消失和新增 pawn", VerifyRaidSettlementIgnoresPawns),
    ("袭击结算忽略策略扩展指定的对象", VerifyRaidSettlementIgnoresPolicyDefs),
    ("袭击结算纳入防守方种植区植物损失", VerifyRaidSettlementIncludesGrowingZonePlants),
    ("袭击结算编辑防守方快照而不是保存战斗快照", VerifyRaidSettlementSnapshotEditor),
    ("袭击结算把战场覆盖层带回防守方快照", VerifyRaidSettlementSnapshotEditorAddsBattlefieldResidues),
    ("第三方存储兼容解析容器内物品", VerifyAdaptiveStorageSaveIndexExtension),
    ("第三方载具兼容解析货物和乘员", VerifyVehicleFrameworkSaveIndexExtension),
    ("第三方载具货物按原始ID参与袭击结算", VerifyVehicleFrameworkCargoOriginalIdsSettle),
    ("第三方载具被摧毁时库存按全损移除", VerifyVehicleFrameworkDestroyedVehicleCargoSettlesAsFullLoss),
    ("第三方载具结算只降低组件生命值", VerifyVehicleFrameworkSettlementComponentDamage),
    ("第三方载具仍存在时保留组件生命损伤", VerifyVehicleFrameworkExistingComponentDamage),
    ("可打包建筑未全损时降低生命值", VerifyPackableBuildingSettlementDamage),
    ("可打包建筑降耐久遵守最低生命比例", VerifyPackableBuildingMinimumRemainingHitPointsRatio),
    ("不可打包建筑消失时只降低生命值", VerifyNonPackableBuildingSettlementDamage),
    ("袭击普通保存证据缺少原始ID时不可打包建筑仍只降低生命值", VerifyOrdinaryRaidEvidenceMissingOriginalIdKeepsNonPackableBuilding),
    ("缺少最大生命值基线的建筑跳过袭击损失", VerifyUnknownBuildingWithoutHitPointBaselineIsSkipped),
    ("袭击结算硬保护逆重核心和特殊对象", VerifyHardProtectedSettlementThings),
    ("地雷触发或被摧毁时按全损删除", VerifyRaidTrapSettlementFullLoss),
    ("未触发隐藏地雷代理保留原地雷结算身份", VerifyUntriggeredHiddenTrapProxySettlementIdentity),
    ("仍存在的受伤建筑按结算上限保留耐久损失", VerifyExistingBuildingSettlementDamage),
    ("袭击返回快照拒绝身份和地图不匹配", VerifyRaidSettlementReturnRejections),
    ("超时袭击攻击者快照只清理远程战场", VerifyRaidAttackerSnapshotCleanupOnlyRemovesRaidBattleMap),
    ("空远程地图会话不会触发袭击清理", VerifyRaidAttackerSnapshotCleanupIgnoresNullRemoteMapSession),
    ("消失判定忽略新增对象", VerifyDisappearanceDiff),
    ("同一对象堆叠减少按减少量结算", VerifyStackReductionDiff),
    ("单件和奇数堆叠使用确定性随机取整", VerifyFractionalStackLoss)
};

foreach ((string name, Action run) in tests)
{
    run();
    Console.WriteLine($"通过：{name}");
}

return 0;

static void VerifyServerDatabaseMigrationInitializesNewDatabase()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-schema-" + Guid.NewGuid().ToString("N") + ".sqlite");
    try
    {
        ServerDatabaseMigrationResult result = ServerDatabaseMigrator.Migrate(path);
        Equal(0, result.StartingVersion, "新数据库起始版本");
        Equal(ServerDatabaseSchema.CurrentVersion, result.FinalVersion, "新数据库最终版本");
        Require(result.CreatedNewDatabase, "新数据库应标记为新建");
        Equal(ServerDatabaseSchema.CurrentVersion, ServerDatabaseMigrator.ReadVersion(path), "新数据库持久化版本");
    }
    finally
    {
        DeleteSqliteDatabase(path);
    }
}

static void VerifyServerDatabaseMigrationUpgradesLegacyDatabase()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-schema-legacy-" + Guid.NewGuid().ToString("N") + ".sqlite");
    try
    {
        using (var connection = OpenSqliteDatabase(path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                create table server_documents (
                    document_key text primary key not null,
                    content_json text not null,
                    updated_at_utc text not null
                );
                create table server_binary_documents (
                    document_key text not null,
                    binary_key text not null,
                    content_blob blob not null,
                    updated_at_utc text not null,
                    primary key (document_key, binary_key)
                );
                insert into server_documents values ('players', '{"preserve":true}', '2026-01-01T00:00:00.0000000+00:00');
                insert into server_binary_documents values ('world-configuration', 'tile-geometry', X'0102', '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        ServerDatabaseMigrationResult result = ServerDatabaseMigrator.Migrate(path);
        Equal(ServerDatabaseSchema.LegacyUnversionedVersion, result.StartingVersion, "旧数据库推断版本");
        Equal(ServerDatabaseSchema.CurrentVersion, result.FinalVersion, "旧数据库最终版本");
        Require(result.AppliedMigrations.Contains("1->2", StringComparer.Ordinal), "旧数据库应执行 1->2 迁移");
        Require(result.RequiresWorldSubstrateRebaseline, "缺失可用管理员快照时应要求重新覆盖世界基线");

        using SqliteConnection verification = OpenSqliteDatabase(path);
        using SqliteCommand verify = verification.CreateCommand();
        verify.CommandText = "select count(*) from server_binary_documents where document_key = 'world-configuration' and binary_key = 'tile-geometry';";
        Equal(0L, (long)verify.ExecuteScalar()!, "旧 tile geometry 应被迁移清理");
        verify.CommandText = "select count(*) from server_binary_documents where document_key = 'world-configuration' and binary_key = 'world-substrate';";
        Equal(0L, (long)verify.ExecuteScalar()!, "没有可用管理员快照时不可伪造新版世界底图包");
        verify.CommandText = "select content_json from server_documents where document_key = 'players';";
        Equal("{\"preserve\":true}", (string)verify.ExecuteScalar()!, "无关注册表数据应保留");
    }
    finally
    {
        DeleteSqliteDatabase(path);
    }
}

static void VerifyServerDatabaseMigrationRejectsFutureVersion()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-schema-future-" + Guid.NewGuid().ToString("N") + ".sqlite");
    try
    {
        using (SqliteConnection connection = OpenSqliteDatabase(path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                create table server_schema_metadata (
                    metadata_key text primary key not null,
                    metadata_value text not null,
                    updated_at_utc text not null
                );
                insert into server_schema_metadata values ('schema_version', '{{ServerDatabaseSchema.CurrentVersion + 1}}', '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        bool rejected = false;
        try
        {
            ServerDatabaseMigrator.Migrate(path);
        }
        catch (InvalidOperationException exception)
        {
            rejected = exception.Message.Contains("newer", StringComparison.OrdinalIgnoreCase);
        }

        Require(rejected, "未来 schema 版本必须拒绝启动");
    }
    finally
    {
        DeleteSqliteDatabase(path);
    }
}

static void VerifyServerDatabaseMigrationRecoversWorldSubstrate()
{
    string root = Path.Combine(Path.GetTempPath(), "clashofrim-schema-recovery-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "server.sqlite");
    string snapshotRoot = Path.Combine(root, "snapshots");
    try
    {
        Directory.CreateDirectory(root);
        byte[] tileGeometry = WorldTileGeometryBinaryCodec.Encode(
            new WorldTileGeometryDto(
                new[]
                {
                    new WorldTileLayerGeometryDto(
                        0,
                        "Surface",
                        1f,
                        new[] { new WorldTileCenterDto(0, 1f, 2f, 3f) })
                }))!;

        using (SqliteConnection connection = OpenSqliteDatabase(path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                create table server_binary_documents (
                    document_key text not null,
                    binary_key text not null,
                    content_blob blob not null,
                    updated_at_utc text not null,
                    primary key (document_key, binary_key)
                );
                create table server_keyed_json_records (
                    collection_key text not null,
                    item_key text not null,
                    content_json text not null,
                    updated_at_utc text not null,
                    primary key (collection_key, item_key)
                );
                insert into server_keyed_json_records values ('world-configuration', 'state', '{"AdministratorUserId":"admin","AdministratorUserIds":["admin"]}', '2026-01-01T00:00:00.0000000+00:00');
                insert into server_binary_documents values ('world-configuration', 'tile-geometry', $tile_geometry, '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$tile_geometry", tileGeometry);
            command.ExecuteNonQuery();
        }

        var store = new FileColonySnapshotIndexStore(snapshotRoot);
        SaveSnapshotPackage package = WorldSubstrateMigrationPackage(
            new SnapshotIdentity("admin", "colony-admin", "admin-world-snapshot"));
        store.StoreLatest(package, package.Index, DateTimeOffset.UnixEpoch);

        ServerDatabaseMigrationResult result = ServerDatabaseMigrator.Migrate(path, snapshotRoot);
        Require(!result.RequiresWorldSubstrateRebaseline, "管理员快照存在时不应要求重新覆盖世界基线");
        Equal("admin-world-snapshot", result.RecoveredWorldSubstrateSnapshotId, "恢复来源快照");

        using SqliteConnection verification = OpenSqliteDatabase(path);
        using SqliteCommand verify = verification.CreateCommand();
        verify.CommandText = "select content_blob from server_binary_documents where document_key = 'world-configuration' and binary_key = 'world-substrate';";
        byte[] payload = (byte[])verify.ExecuteScalar()!;
        Require(WorldSubstratePackageCodec.TryDecode(payload, out WorldSubstratePackage? substrate, out string? failure), "恢复后的世界底图包应可解码: " + failure);
        Require(substrate is not null, "恢复后的世界底图包不可为空");
        Require(WorldTileGeometryBinaryCodec.Decode(substrate!.TileGeometryPayload)?.Layers.Single().TileCenters.Count == 1, "旧版地块几何应保留在恢复后的世界底图包中");
    }
    finally
    {
        DeleteSqliteDatabase(path);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static SqliteConnection OpenSqliteDatabase(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString());
    connection.Open();
    return connection;
}

static void DeleteSqliteDatabase(string path)
{
    SqliteConnection.ClearAllPools();
    foreach (string candidate in new[] { path, path + "-shm", path + "-wal" })
    {
        if (File.Exists(candidate))
        {
            File.Delete(candidate);
        }
    }
}

static SaveSnapshotPackage WorldSubstrateMigrationPackage(SnapshotIdentity identity)
{
    byte[] payload = Encoding.UTF8.GetBytes(
        """
        <savegame>
          <game>
            <world>
              <info><persistentRandomValue>12345</persistentRandomValue></info>
              <grid><layers><keys><li>0</li></keys><values><li Class="SurfaceLayer"><def>Surface</def><layerId>0</layerId></li></values></layers></grid>
              <features />
              <landmarks />
            </world>
          </game>
        </savegame>
        """);
    var envelope = new SaveSnapshotEnvelope(
        SaveSnapshotPackageBuilder.CurrentPackageVersion,
        identity,
        DateTimeOffset.UnixEpoch,
        "administrator-world.rws",
        "1.6-test",
        SnapshotPayloadEncoding.RawRws,
        payload.Length,
        payload.Length,
        new string('a', 64),
        new string('b', 64));
    return new SaveSnapshotPackage(envelope, payload, SnapshotWithThings());
}

static void VerifyWorldSubstratePackageRoundTrip()
{
    byte[] save = Encoding.UTF8.GetBytes(
        """
        <savegame>
          <game>
            <world>
              <info><persistentRandomValue>12345</persistentRandomValue></info>
              <grid><nextLayerId>2</nextLayerId><layers><keys><li>0</li></keys><values><li Class="SurfaceLayer"><def>Surface</def><layerId>0</layerId><subdivisions>10</subdivisions><tileBiomeDeflate>AA==</tileBiomeDeflate></li></values></layers></grid>
              <features><features><li><def>Island</def><uniqueID>7</uniqueID><name>AdministratorName</name><layer>PlanetLayer_0</layer></li></features></features>
              <landmarks><landmarks><keys><li>42,0</li></keys><values><li><def>TestLandmark</def><name>AdministratorLandmark</name></li></values></landmarks></landmarks>
              <worldPawns><pawns><li>unsafe</li></pawns></worldPawns>
              <worldObjects><worldObjects><li>unsafe</li></worldObjects></worldObjects>
              <components><li>unsafe</li></components>
            </world>
          </game>
        </savegame>
        """);

    Require(WorldSubstratePackage.TryExtract(save, out WorldSubstratePackage? extracted, out string? failure), "世界底图包应可从有效快照提取: " + failure);
    Require(extracted is not null, "世界底图包不可为空");
    Equal(12345, extracted!.PersistentRandomValue, "世界稳定随机值");
    Require(extracted.GridXml.Contains("tileBiomeDeflate", StringComparison.Ordinal), "世界底图包应保留原版压缩地块数组");
    Require(extracted.FeaturesXml.Contains("uniqueID>7", StringComparison.Ordinal), "世界底图包应保留地貌唯一标识");
    Require(extracted.LandmarksXml.Contains("42,0", StringComparison.Ordinal), "世界底图包应保留地标位置");
    Require(!extracted.GridXml.Contains("worldPawns", StringComparison.Ordinal), "世界底图包不得包含 world pawn");

    extracted = new WorldSubstratePackage(
        extracted.PersistentRandomValue,
        extracted.GridXml,
        extracted.FeaturesXml,
        extracted.LandmarksXml,
        new byte[] { 1, 2, 3, 4, 5 });
    byte[] encoded = WorldSubstratePackageCodec.Encode(extracted);
    Require(WorldSubstratePackageCodec.TryDecode(encoded, out WorldSubstratePackage? decoded, out failure), "世界底图包应可解码: " + failure);
    Require(decoded is not null, "解码后的世界底图包不可为空");
    Equal(extracted.GridXml, decoded!.GridXml, "世界网格压缩往返");
    Equal(extracted.FeaturesXml, decoded.FeaturesXml, "世界特征压缩往返");
    Equal(extracted.LandmarksXml, decoded.LandmarksXml, "世界地标压缩往返");
    Require(decoded.TileGeometryPayload.SequenceEqual(extracted.TileGeometryPayload), "世界地图几何二进制压缩往返");
}

static void VerifyWorldSubstratePackageRejectsMissingGrid()
{
    byte[] save = Encoding.UTF8.GetBytes("<savegame><game><world><info /></world></game></savegame>");
    Require(!WorldSubstratePackage.TryExtract(save, out _, out string? failure), "缺失世界网格的快照必须被拒绝");
    Require(!string.IsNullOrWhiteSpace(failure), "拒绝原因不可为空");
}

static void VerifyWorldSubstrateRegistryReadiness()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-world-substrate-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var registry = new WorldConfigurationRegistry(path);
        WorldSessionState initial = registry.Prepare("admin");
        var geometry = new WorldTileGeometryDto(new[]
        {
            new WorldTileLayerGeometryDto(
                0,
                "Surface",
                1f,
                new[] { new WorldTileCenterDto(0, 1f, 0f, 0f) })
        });
        var configuration = new WorldConfigurationDto(
            "world:test",
            "admin",
            "colony-a",
            DateTimeOffset.UtcNow,
            "test-seed",
            "0.3",
            "Normal",
            "Normal",
            "Normal",
            "Normal",
            "10000",
            new[] { "OutlanderCivil" },
            Array.Empty<WorldFeatureDto>(),
            Array.Empty<WorldFactionDto>(),
            Array.Empty<WorldRoadDto>(),
            Array.Empty<WorldObjectBaselineDto>(),
            Array.Empty<PlayerColonySiteDto>());
        WorldSessionState submitted = registry.Submit("admin", configuration);
        Require(!submitted.WorldConfigured, "仅提交轻量世界配置时不应开放世界会话");

        byte[] geometryPayload = WorldTileGeometryBinaryCodec.Encode(geometry)!;
        byte[] payload = WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(
            123,
            "<grid><layers><keys><li>0</li></keys><values><li /></values></layers></grid>",
            "<features />",
            "<landmarks />",
            geometryPayload));
        WorldSubstrateStoreResult stored = registry.StoreWorldSubstrate("admin", "colony-a", "world:test", payload);
        Require(stored.Accepted, "管理员底图包应被接受");
        Require(registry.Prepare("player-b").WorldConfigured, "底图包提交后应开放世界会话");

        var reopened = new WorldConfigurationRegistry(path);
        Require(reopened.Prepare("player-c").WorldConfigured, "重启后应恢复世界就绪状态");
        Require(reopened.Current?.TileGeometry?.Layers.Count == 1, "重启后应从世界底图恢复几何缓存");
        Require(reopened.TryGetWorldSubstrate("world:test", out byte[]? restored) && restored is not null, "重启后应可下载世界底图包");
    }
    finally
    {
        string binaryRoot = Path.ChangeExtension(path, null) + ".binary";
        try
        {
            File.Delete(path);
            if (Directory.Exists(binaryRoot))
            {
                Directory.Delete(binaryRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

static void VerifyDeflatedWealthHistoryFallback()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-deflated-wealth-" + Guid.NewGuid().ToString("N") + ".rws");
    try
    {
        string encodedRecords = Convert.ToBase64String(DeflateBytes(FloatRecords(1000f, 23456.75f)));
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <savegame>
              <meta><gameVersion>1.6-test</gameVersion></meta>
              <game>
                <history>
                  <autoRecorderGroups>
                    <li>
                      <def>Wealth</def>
                      <recorders>
                        <li>
                          <def>Wealth_Total</def>
                          <recordsDeflate>{encodedRecords}</recordsDeflate>
                        </li>
                      </recorders>
                    </li>
                  </autoRecorderGroups>
                </history>
                <maps>
                  <li>
                    <uniqueID>0</uniqueID>
                    <mapInfo><parent>WorldObject_1</parent></mapInfo>
                    <things />
                  </li>
                </maps>
              </game>
            </savegame>
            """);

        SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(path, new SaveIndexReadOptions
        {
            Identity = new SnapshotIdentity("userA", "colonyA", "deflated-wealth")
        });

        Equal(1, index.Maps.Count, "压缩财富历史样本地图数量");
        Require(index.Maps[0].WealthTotal.HasValue, "压缩财富历史应可作为地图财富兜底");
        Equal(23457, (int)Math.Round(index.Maps[0].WealthTotal!.Value, MidpointRounding.AwayFromZero), "压缩财富历史最后记录");
    }
    finally
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

static void VerifyPlayerColonistCountIndex()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-player-colonists-" + Guid.NewGuid().ToString("N") + ".rws");
    try
    {
        string encodedPopulationRecords = Convert.ToBase64String(FloatRecords(20f));
        string encodedPrisonerRecords = Convert.ToBase64String(FloatRecords(10f));
        string encodedWealthItemRecords = Convert.ToBase64String(FloatRecords(250000f));
        string encodedWealthBuildingRecords = Convert.ToBase64String(FloatRecords(260000f));
        string encodedWealthPawnRecords = Convert.ToBase64String(FloatRecords(200000f));
        string encodedMoodRecords = Convert.ToBase64String(FloatRecords(91f));
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <savegame>
              <meta><gameVersion>1.6-test</gameVersion></meta>
              <game>
                <history>
                  <autoRecorderGroups>
                    <li>
                      <def>Wealth</def>
                      <recorders>
                        <li>
                          <def>Wealth_Items</def>
                          <records>{encodedWealthItemRecords}</records>
                        </li>
                        <li>
                          <def>Wealth_Buildings</def>
                          <records>{encodedWealthBuildingRecords}</records>
                        </li>
                        <li>
                          <def>Wealth_Pawns</def>
                          <records>{encodedWealthPawnRecords}</records>
                        </li>
                      </recorders>
                    </li>
                    <li>
                      <def>Population</def>
                      <recorders>
                        <li>
                          <def>FreeColonists</def>
                          <records>{encodedPopulationRecords}</records>
                        </li>
                        <li>
                          <def>Prisoners</def>
                          <records>{encodedPrisonerRecords}</records>
                        </li>
                      </recorders>
                    </li>
                    <li>
                      <def>ColonistMood</def>
                      <recorders>
                        <li>
                          <def>ColonistMood</def>
                          <records>{encodedMoodRecords}</records>
                        </li>
                      </recorders>
                    </li>
                  </autoRecorderGroups>
                </history>
                <scenario>
                  <playerFaction>
                    <factionDef>PlayerColony</factionDef>
                  </playerFaction>
                </scenario>
                <storyWatcher>
                  <statsRecord>
                    <colonistsLaunched>7</colonistsLaunched>
                  </statsRecord>
                </storyWatcher>
                <world>
                  <factionManager>
                    <allFactions>
                      <li>
                        <def>PlayerColony</def>
                        <loadID>7</loadID>
                      </li>
                      <li>
                        <def>OutlanderCivil</def>
                        <loadID>9</loadID>
                      </li>
                    </allFactions>
                  </factionManager>
                </world>
                <maps>
                  <li>
                    <uniqueID>0</uniqueID>
                    <mapInfo><parent>WorldObject_1</parent><size>(250, 1, 250)</size></mapInfo>
                    <things>
                      <thing Class="Pawn">
                        <id>Human1</id>
                        <def>Human</def>
                        <kindDef>Colonist</kindDef>
                        <faction>Faction_7</faction>
                        <story />
                      </thing>
                      <thing Class="Pawn">
                        <id>Human2</id>
                        <def>Human</def>
                        <kindDef>Colonist</kindDef>
                        <faction>Faction_7</faction>
                        <story />
                      </thing>
                      <thing Class="Pawn">
                        <id>DeadHuman</id>
                        <def>Human</def>
                        <kindDef>Colonist</kindDef>
                        <faction>Faction_7</faction>
                        <story />
                        <healthTracker><healthState>Dead</healthState></healthTracker>
                      </thing>
                      <thing Class="Pawn">
                        <id>Animal1</id>
                        <def>Dog</def>
                        <kindDef>Husky</kindDef>
                        <faction>Faction_7</faction>
                      </thing>
                      <thing Class="Pawn">
                        <id>Visitor1</id>
                        <def>Human</def>
                        <kindDef>Town_Councilman</kindDef>
                        <faction>Faction_9</faction>
                        <story />
                      </thing>
                    </things>
                  </li>
                </maps>
              </game>
            </savegame>
            """);

        SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(path);
        Equal(20, index.HistoryPlayerColonistCount, "玩家殖民者人数应优先读取原版 Population/FreeColonists 历史记录。");
        Equal(10, index.HistoryPrisonerCount, "囚犯人数应优先读取原版 Population/Prisoners 历史记录。");
        Equal(250000, index.HistoryWealthItems, "物品财富应读取原版 Wealth/Wealth_Items 历史记录。");
        Equal(260000, index.HistoryWealthBuildings, "建筑财富应读取原版 Wealth/Wealth_Buildings 历史记录。");
        Equal(200000, index.HistoryWealthPawns, "人员财富应读取原版 Wealth/Wealth_Pawns 历史记录。");
        Equal(91, index.HistoryColonistMood, "殖民者心情应读取原版 ColonistMood/ColonistMood 历史记录。");
        Equal(7, index.StoryColonistsLaunched, "飞船和终局送走人数应读取原版 storyWatcher/statsRecord/colonistsLaunched。");
        Equal(2, index.Maps.Single().PlayerColonistCount, "玩家殖民者人数应只统计玩家阵营的存活类人 pawn。");
        Equal(5, index.Maps.Single().PawnCount, "总 pawn 数仍保留原始统计。");
    }
    finally
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

static void VerifySnapshotPackageEnvelope()
{
    string samplePath = WriteTemporaryFixtureSave();

    try
    {
        var identity = new SnapshotIdentity("userA", "colonyA", "snapshot-package-001");
        SaveSnapshotPackage package = SaveSnapshotPackageBuilder.FromFile(
            samplePath,
            identity,
            DateTimeOffset.UnixEpoch,
            SnapshotPayloadEncoding.GzipRws);
        SaveSnapshotPackage repeated = SaveSnapshotPackageBuilder.FromFile(
            samplePath,
            identity,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            SnapshotPayloadEncoding.GzipRws);
        SaveSnapshotPackage raw = SaveSnapshotPackageBuilder.FromFile(
            samplePath,
            identity,
            DateTimeOffset.UnixEpoch,
            SnapshotPayloadEncoding.RawRws);

        Equal(SaveSnapshotPackageBuilder.CurrentPackageVersion, package.Envelope.PackageVersion, "快照封装版本");
        Equal(identity, package.Envelope.Identity, "快照身份");
        Equal(Path.GetFileName(samplePath), package.Envelope.SourceFileName, "源文件名");
        Equal(package.Index.Meta.GameVersion, package.Envelope.RimWorldVersion, "RimWorld 版本");
        Equal(new FileInfo(samplePath).Length, package.Envelope.OriginalSaveBytes, "原始存档大小");
        Equal(package.Payload.LongLength, package.Envelope.PayloadBytes, "载荷大小");
        Equal(SnapshotPayloadEncoding.GzipRws, package.Envelope.PayloadEncoding, "压缩载荷编码");
        Equal(package.Envelope.OriginalSha256, repeated.Envelope.OriginalSha256, "同一存档原始哈希稳定");
        Equal(package.Envelope.PayloadSha256, repeated.Envelope.PayloadSha256, "同一存档压缩载荷哈希稳定");
        Equal(raw.Envelope.OriginalSha256, raw.Envelope.PayloadSha256, "原始载荷哈希应等于原始存档哈希");
        Require(package.Payload.Length > 0, "封装载荷不能为空");
        Require(package.Index.Things.Count > 0, "封装应同时生成索引");
        Require(package.Index.Things.Any(thing => thing.GlobalKey.Contains("snapshot:snapshot-package-001", StringComparison.Ordinal)), "索引应使用封装身份");
    }
    finally
    {
        File.Delete(samplePath);
    }
}

static void VerifySnapshotUploadAccepted()
{
    SaveSnapshotPackage package = BuildFixturePackage("snapshot-upload-001");
    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(
        store,
        new SnapshotUploadPolicy(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            package.Envelope.RimWorldVersion!
        }));

    SnapshotUploadResult result = receiver.Receive(
        new SnapshotUploadContext("userA", "colonyA", "snapshot-upload-001"),
        package,
        DateTimeOffset.UnixEpoch.AddHours(1));

    Require(result.Accepted, "快照上传应通过校验");
    Equal(SnapshotUploadResultKind.Accepted, result.Kind, "上传结果");
    Require(result.AcceptedSnapshot is not null, "通过结果应返回快照记录");

    LatestSnapshotRecord? latest = store.GetLatest("userA", "colonyA");
    Require(latest is not null, "通过校验的快照应进入最新殖民地索引");
    Equal("snapshot-upload-001", latest!.Identity.SnapshotId, "最新快照 ID");
    Equal(DateTimeOffset.UnixEpoch.AddHours(1), latest.AcceptedAtUtc, "接受时间");
    Require(latest.Index.Things.Any(thing => thing.GlobalKey.Contains("snapshot:snapshot-upload-001", StringComparison.Ordinal)), "服务端重建索引应使用上传身份");
}

static void VerifyFileSnapshotStorePersistence()
{
    string root = Path.Combine(Path.GetTempPath(), "clash-of-rim-file-snapshot-store-" + Guid.NewGuid().ToString("N"));
    try
    {
        SaveSnapshotPackage package = BuildFixturePackage("snapshot-file-store-001");
        var store = new FileColonySnapshotIndexStore(root);
        var receiver = new SnapshotUploadReceiver(store, SnapshotUploadPolicy.AllowAnyVersion);

        SnapshotUploadResult result = receiver.Receive(
            new SnapshotUploadContext("userA", "colonyA", "snapshot-file-store-001"),
            package,
            DateTimeOffset.UnixEpoch.AddDays(1));
        Require(result.Accepted, "文件快照存储应接受合法上传");

        string packageDirectory = Path.Combine(root, "packages");
        Equal(1, Directory.GetFiles(packageDirectory, "*.snapshot.gz").Length, "单文件快照包数量");
        Require(!Directory.Exists(Path.Combine(root, "payloads")), "不应再创建旧压缩快照载荷目录");
        Require(!Directory.Exists(Path.Combine(root, "metadata")), "不应再创建旧压缩快照元数据目录");

        var reopened = new FileColonySnapshotIndexStore(root);
        LatestSnapshotRecord? latest = reopened.GetLatest("userA", "colonyA");
        Require(latest is not null, "重启后应能恢复最新快照索引");
        Equal("snapshot-file-store-001", latest!.Identity.SnapshotId, "恢复后的快照 ID");
        Equal(DateTimeOffset.UnixEpoch.AddDays(1), latest.AcceptedAtUtc, "恢复后的接受时间");
        Equal(package.Envelope.PayloadSha256, latest.Envelope.PayloadSha256, "恢复后的上传载荷哈希");
        Equal(0, latest.Index.Things.Count, "恢复后的轻量索引不应持久化完整 thing 列表");

        SaveSnapshotPackage? reopenedPackage = reopened.GetLatestPackage("userA", "colonyA");
        Require(reopenedPackage is not null, "重启后应能从单文件快照包恢复载荷");
        Require(reopenedPackage!.Index.Things.Any(thing => thing.GlobalKey.Contains("snapshot:snapshot-file-store-001", StringComparison.Ordinal)), "按需读取包时应从 rws 重建完整 thing 索引");

        Require(reopened.RemoveLatest("userA", "colonyA"), "文件快照存储应能删除最新快照");
        Equal(null, reopened.GetLatest("userA", "colonyA"), "删除后当前实例不应返回最新快照");
        Equal(0, Directory.GetFiles(packageDirectory, "*.snapshot.gz").Length, "删除后单文件快照包数量");

        var reopenedAfterRemove = new FileColonySnapshotIndexStore(root);
        Equal(null, reopenedAfterRemove.GetLatest("userA", "colonyA"), "删除后重启不应恢复旧快照");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void VerifySnapshotPackageFileReader()
{
    string root = Path.Combine(Path.GetTempPath(), "clash-of-rim-package-reader-" + Guid.NewGuid().ToString("N"));
    try
    {
        SaveSnapshotPackage package = BuildFixturePackage("snapshot-package-reader-001");
        var store = new FileColonySnapshotIndexStore(root);
        var receiver = new SnapshotUploadReceiver(store, SnapshotUploadPolicy.AllowAnyVersion);

        SnapshotUploadResult result = receiver.Receive(
            new SnapshotUploadContext("userA", "colonyA", "snapshot-package-reader-001"),
            package,
            DateTimeOffset.UnixEpoch.AddDays(2));
        Require(result.Accepted, "单文件快照包测试应先完成合法上传");

        string packagePath = Path.Combine(
            root,
            "packages",
            SaveSnapshotPackageFileReader.PackageFileName("userA", "colonyA"));
        Require(File.Exists(packagePath), "当前格式快照包应落盘为 .snapshot.gz");

        SaveSnapshotPackageFileReadResult? read = SaveSnapshotPackageFileReader.ReadPackage(packagePath);
        Require(read is not null, "新版快照包读者应能解析 .snapshot.gz");
        Equal("snapshot-package-reader-001", read!.Persisted.Identity.SnapshotId, "读者恢复快照 ID");
        Equal(DateTimeOffset.UnixEpoch.AddDays(2), read.Persisted.AcceptedAtUtc, "读者恢复接受时间");
        Equal(package.Envelope.PayloadSha256, read.Persisted.Envelope.PayloadSha256, "读者恢复 envelope payload hash");
        Require(read.EncodedPayload is not null, "读者应恢复编码载荷");
        Equal(package.Payload.LongLength, read.EncodedPayload!.LongLength, "读者恢复编码载荷长度");
        Require(read.RebuiltIndex is not null, "读者应从包内 rws 重建完整索引");
        Require(read.RebuiltIndex!.Things.Any(thing => thing.GlobalKey.Contains("snapshot:snapshot-package-reader-001", StringComparison.Ordinal)), "读者重建索引应使用包内身份");
        Require(read.Package is not null, "读者应能返回完整 SaveSnapshotPackage");

        Equal(
            packagePath,
            SaveSnapshotPackageFileReader.FindPackagePath(root, "userA", "colonyA"),
            "读者应能按服务器 Data 根目录定位包");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void VerifySnapshotUploadRejections()
{
    SaveSnapshotPackage package = BuildFixturePackage("snapshot-upload-reject");
    var context = new SnapshotUploadContext("userA", "colonyA", "snapshot-upload-reject");

    Equal(
        SnapshotUploadResultKind.MissingIdentity,
        Receive(package with { Envelope = package.Envelope with { Identity = new SnapshotIdentity(null, "colonyA", "snapshot-upload-reject") } }, context).Kind,
        "缺少玩家身份应拒绝");

    Equal(
        SnapshotUploadResultKind.IdentityMismatch,
        Receive(package, new SnapshotUploadContext("userA", "colonyA", "wrong-snapshot")).Kind,
        "快照 ID 与上下文不一致应拒绝");

    Equal(
        SnapshotUploadResultKind.MissingHash,
        Receive(package with { Envelope = package.Envelope with { PayloadSha256 = "" } }, context).Kind,
        "缺少载荷哈希应拒绝");

    byte[] corruptedPayload = package.Payload.ToArray();
    corruptedPayload[0] ^= 0x7f;
    Equal(
        SnapshotUploadResultKind.PayloadHashMismatch,
        Receive(package with { Payload = corruptedPayload }, context).Kind,
        "载荷哈希不一致应拒绝");

    Equal(
        SnapshotUploadResultKind.OriginalHashMismatch,
        Receive(package with { Envelope = package.Envelope with { OriginalSha256 = new string('0', 64) } }, context).Kind,
        "原始存档哈希不一致应拒绝");

    Equal(
        SnapshotUploadResultKind.MissingRimWorldVersion,
        Receive(package with { Envelope = package.Envelope with { RimWorldVersion = null } }, context).Kind,
        "缺少 RimWorld 版本应拒绝");

    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(
        store,
        new SnapshotUploadPolicy(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "not-current-version" }));

    SnapshotUploadResult versionResult = receiver.Receive(context, package, DateTimeOffset.UnixEpoch);
    Equal(SnapshotUploadResultKind.IncompatibleRimWorldVersion, versionResult.Kind, "不兼容版本应拒绝");
    Equal(null, store.GetLatest("userA", "colonyA"), "被拒绝快照不应进入最新索引");
}

static void VerifyRaidSettlementEvidenceUploadRequiresEventMap()
{
    SaveSnapshotPackage missingMap = BuildFixturePackage("snapshot-raid-evidence-missing-map");
    SnapshotUploadResult missing = Receive(
        missingMap,
        new SnapshotUploadContext(
            "userA",
            "colonyA",
            "snapshot-raid-evidence-missing-map",
            SnapshotUploadKind: "RaidSettlementEvidence",
            RequiredRaidEventId: "raid-001"));
    Equal(SnapshotUploadResultKind.InvalidPayload, missing.Kind, "缺少指定袭击战场地图的结算证据应拒绝");

    SaveSnapshotPackage withMap = BuildFixturePackageWithRaidBattleMap("snapshot-raid-evidence-with-map", "raid-001");
    SnapshotUploadResult accepted = Receive(
        withMap,
        new SnapshotUploadContext(
            "userA",
            "colonyA",
            "snapshot-raid-evidence-with-map",
            SnapshotUploadKind: "RaidSettlementEvidence",
            RequiredRaidEventId: "raid-001"));
    Require(accepted.Accepted, "携带指定袭击战场地图的结算证据应通过上传校验");

    SnapshotUploadResult wrongEvent = Receive(
        withMap,
        new SnapshotUploadContext(
            "userA",
            "colonyA",
            "snapshot-raid-evidence-with-map",
            SnapshotUploadKind: "RaidSettlementEvidence",
            RequiredRaidEventId: "raid-other"));
    Equal(SnapshotUploadResultKind.InvalidPayload, wrongEvent.Kind, "袭击战场地图事件 ID 不匹配应拒绝");
}

static SnapshotUploadResult Receive(SaveSnapshotPackage package, SnapshotUploadContext context)
{
    var store = new InMemoryColonySnapshotIndexStore();
    var receiver = new SnapshotUploadReceiver(store, SnapshotUploadPolicy.AllowAnyVersion);
    return receiver.Receive(context, package, DateTimeOffset.UnixEpoch);
}

static SaveSnapshotPackage BuildFixturePackage(string snapshotId)
{
    string samplePath = WriteTemporaryFixtureSave();
    try
    {
        return SaveSnapshotPackageBuilder.FromFile(
            samplePath,
            new SnapshotIdentity("userA", "colonyA", snapshotId),
            DateTimeOffset.UnixEpoch,
            SnapshotPayloadEncoding.GzipRws);
    }
    finally
    {
        File.Delete(samplePath);
    }
}

static SaveSnapshotPackage BuildFixturePackageWithRaidBattleMap(string snapshotId, string raidEventId)
{
    string samplePath = WriteTemporaryFixtureSave();
    try
    {
        XDocument document = XDocument.Load(samplePath, LoadOptions.PreserveWhitespace);
        XElement worldObjects = document.Root!
            .Element("game")!
            .Element("world")!
            .Element("worldObjects")!
            .Element("worldObjects")!;
        worldObjects.Add(
            new XElement("li",
                new XAttribute("Class", "AIRsLight.ClashOfRim.RemoteMaps.RemoteSessionMapParent"),
                new XElement("ID", "77"),
                new XElement("def", "ClashOfRim_RemoteRaidBattleMapParent"),
                new XElement("tile", "12345"),
                new XElement("clashOfRimMode", "RaidBattle"),
                new XElement("clashOfRimRelatedEventId", raidEventId)));

        XElement maps = document.Root!.Element("game")!.Element("maps")!;
        maps.Add(
            new XElement("li",
                new XElement("uniqueID", "Map_77"),
                new XElement("generatedId", "Generated_77"),
                new XElement("mapInfo",
                    new XElement("parent", "WorldObject_77"),
                    new XElement("size", "(250, 1, 250)")),
                new XElement("compressedThingMap"),
                new XElement("terrainGrid"),
                new XElement("roofGrid"),
                new XElement("fogGrid"),
                new XElement("things")));
        document.Save(samplePath);

        return SaveSnapshotPackageBuilder.FromFile(
            samplePath,
            new SnapshotIdentity("userA", "colonyA", snapshotId),
            DateTimeOffset.UnixEpoch,
            SnapshotPayloadEncoding.GzipRws);
    }
    finally
    {
        File.Delete(samplePath);
    }
}

static string WriteTemporaryFixtureSave()
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-fixture-" + Guid.NewGuid().ToString("N") + ".rws");
    File.WriteAllText(
        path,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds />
            <modSteamIds />
            <modNames />
          </meta>
          <game>
            <tickManager>
              <ticksGame>1000</ticksGame>
            </tickManager>
            <world>
              <factionManager>
                <allFactions>
                  <li>
                    <loadID>0</loadID>
                    <def>PlayerColony</def>
                    <name>Fixture Colony</name>
                  </li>
                </allFactions>
              </factionManager>
              <worldObjects>
                <worldObjects>
                  <li Class="RimWorld.Settlement">
                    <ID>1</ID>
                    <def>Settlement</def>
                    <tile>12345</tile>
                    <faction>Faction_0</faction>
                    <nameInt>Fixture Site</nameInt>
                  </li>
                </worldObjects>
              </worldObjects>
            </world>
            <maps>
              <li>
                <uniqueID>Map_0</uniqueID>
                <generatedId>Generated_0</generatedId>
                <mapInfo>
                  <parent>WorldObject_1</parent>
                  <size>(250, 1, 250)</size>
                </mapInfo>
                <compressedThingMap />
                <terrainGrid />
                <roofGrid />
                <fogGrid />
                <things>
                  <thing Class="ThingWithComps">
                    <id>Thing_Steel_1</id>
                    <def>Steel</def>
                    <pos>(10, 0, 10)</pos>
                    <faction>Faction_0</faction>
                    <stackCount>75</stackCount>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    return path;
}

static void VerifyReadOnlyObservationBoundary()
{
    SaveSnapshotPackage package = BuildFixturePackage("snapshot-observe-001");
    var store = new InMemoryColonySnapshotIndexStore();
    var latest = new LatestSnapshotRecord(
        package.Envelope.Identity,
        package.Envelope,
        package.Index,
        DateTimeOffset.UnixEpoch);
    store.StoreLatest(latest);

    MapSummary map = package.Index.Maps[0];
    var request = new ReadOnlyObservationRequest(
        "observer-user",
        package.Envelope.Identity,
        new ObservationMapContext(map.UniqueId!, map.ParentWorldObjectId));
    var loader = new ReadOnlyObservationLoader(store);

    ObservationLoadResult result = loader.Load(request, "observe-session-001");
    Require(result.Granted, "只读观察请求应被授权");
    Require(result.Session is not null, "授权结果应包含观察会话");

    ObservationSession session = result.Session!;
    Equal("observe-session-001", session.SessionId, "观察会话 ID");
    Equal(map.UniqueId, session.Map.UniqueId, "观察地图 ID");
    Require(session.IsActionAllowed(ObservationAction.Inspect), "观察模式应允许查看");
    Require(session.IsActionAllowed(ObservationAction.PanCamera), "观察模式应允许移动视角");
    Require(session.IsActionAllowed(ObservationAction.IssuePawnCommand), "观察沙盒内可以下 pawn 命令");
    Require(session.IsActionAllowed(ObservationAction.DesignateConstruction), "观察沙盒内可以建造规划");
    Require(session.IsActionAllowed(ObservationAction.InteractWithThing), "观察沙盒内可以操作物品");
    Require(session.IsActionAllowed(ObservationAction.AdvanceTime), "观察沙盒内可以推进时间");
    Require(session.IsActionAllowed(ObservationAction.ModifyWorldState), "观察沙盒内可以修改本地世界状态");
    Require(!session.IsActionAllowed(ObservationAction.InjectObservationPawn), "观察模式应禁止注入观察用 pawn");
    Require(!session.IsActionAllowed(ObservationAction.EnterWithRealColonist), "观察模式应禁止真实殖民者进入");
    Require(!session.IsActionAllowed(ObservationAction.UploadSnapshot), "观察模式应禁止上传快照");
    Require(!session.CanSubmitSnapshot, "观察模式不能提交结算快照");

    bool blockedSubmission = false;
    try
    {
        session.CreateUploadContextForSubmission();
    }
    catch (InvalidOperationException)
    {
        blockedSubmission = true;
    }

    Require(blockedSubmission, "观察会话不能生成可提交快照上下文");

    Equal(
        ObservationLoadResultKind.SnapshotNotFound,
        loader.Load(
            request with { TargetSnapshot = package.Envelope.Identity with { SnapshotId = "old-snapshot" } },
            "observe-session-missing").Kind,
        "非最新或不存在的快照不应加载");

    Equal(
        ObservationLoadResultKind.MapNotFound,
        loader.Load(
            request with { TargetMap = new ObservationMapContext("missing-map") },
            "observe-session-map-missing").Kind,
        "不存在的地图不应加载");

    Equal(
        ObservationLoadResultKind.MapContextMismatch,
        loader.Load(
            request with { TargetMap = new ObservationMapContext(map.UniqueId!, "wrong-world-object") },
            "observe-session-map-mismatch").Kind,
        "地图世界对象不匹配不应加载");
}

static void VerifyRaidTrapVisibility()
{
    ThingSummary vanillaTrap = ThingWithDef("trap-spike", "TrapSpike", "Building_TrapDamager");
    ThingSummary modTrap = ThingWithDef("milira-trap", "Milira_TrapRepulsive", "AncotLibrary.Building_TrapRepulsive");
    ThingSummary unapprovedTrapLike = ThingWithDef("trap-name-only", "Unknown_TrapThing", "SomeMod.Building_TrapNamed");
    ThingSummary steel = ThingWithDef("steel-stack", "Steel", "ThingWithComps");
    ThingSummary pawn = ThingWithDef("pawn-colonist", "Human", "Pawn") with { IsPawn = true };

    SaveSnapshotIndex snapshot = SnapshotWithThings(vanillaTrap, modTrap, unapprovedTrapLike, steel, pawn);
    var manifest = new ThingDefTrapClassificationManifest(new[]
    {
        new ThingDefTrapClassification("TrapSpike", "RimWorld.Building_TrapDamager", "Core", InheritsBuildingTrap: true, ApprovedCustomTrap: false),
        new ThingDefTrapClassification("Milira_TrapRepulsive", "AncotLibrary.Building_TrapRepulsive", "Milira", InheritsBuildingTrap: false, ApprovedCustomTrap: true),
        new ThingDefTrapClassification("Unknown_TrapThing", "SomeMod.Building_TrapNamed", "Unknown", InheritsBuildingTrap: false, ApprovedCustomTrap: false)
    });

    RaidTrapVisibilityState state = RaidTrapVisibilityState.FromSnapshot(snapshot, manifest, mapUniqueId: "Map_0");

    Equal(2, state.HiddenTraps.Count, "只有服务器批准的陷阱进入隐藏集合");
    Require(state.ContainsTrap(vanillaTrap.GlobalKey), "原版 Building_Trap 继承链陷阱应被识别");
    Require(state.ContainsTrap(modTrap.GlobalKey), "批准的第三方自定义陷阱应被识别");
    Require(!state.ContainsTrap(unapprovedTrapLike.GlobalKey), "仅名字像陷阱但未批准不应被隐藏");
    Require(!state.ContainsTrap(steel.GlobalKey), "普通物品不应被隐藏");
    Require(!state.ContainsTrap(pawn.GlobalKey), "pawn 不应被陷阱隐藏规则处理");

    foreach (RaidTrapVisibilitySurface surface in Enum.GetValues<RaidTrapVisibilitySurface>())
    {
        Require(state.ShouldHide(vanillaTrap, surface), $"未揭示原版陷阱应隐藏于 {surface}");
        Require(state.ShouldHide(modTrap, surface), $"未揭示第三方陷阱应隐藏于 {surface}");
        Require(!state.ShouldHide(steel, surface), $"普通物品不应隐藏于 {surface}");
    }

    Equal(snapshot.Things.Count, state.GetRetainedThings(snapshot).Count, "隐藏计划不应删除真实 thing");

    RaidTrapRevealResult reveal = state.Reveal(vanillaTrap.GlobalKey, RaidTrapRevealReason.Triggered);
    Require(reveal.Revealed, "首次揭示应改变状态");
    Require(reveal.RequiresMapMeshRefresh, "首次揭示应要求刷新地图 mesh");
    Require(state.IsRevealed(vanillaTrap.GlobalKey), "陷阱应进入已揭示集合");
    Require(!state.ShouldHide(vanillaTrap, RaidTrapVisibilitySurface.MapDrawing), "已触发陷阱不应继续隐藏绘制");
    Require(!state.ShouldHide(vanillaTrap, RaidTrapVisibilitySurface.MouseoverReadout), "已触发陷阱不应继续隐藏悬浮文本");
    Require(!state.ShouldHide(vanillaTrap, RaidTrapVisibilitySurface.Selection), "已触发陷阱不应继续隐藏点选");
    Require(!state.ShouldHide(vanillaTrap, RaidTrapVisibilitySurface.InspectPane), "已触发陷阱不应继续隐藏检查面板");

    RaidTrapRevealResult repeatedReveal = state.Reveal(vanillaTrap.GlobalKey, RaidTrapRevealReason.Scouted);
    Require(!repeatedReveal.Revealed, "重复揭示不应再次改变状态");
    Require(!repeatedReveal.RequiresMapMeshRefresh, "重复揭示不应再次要求刷新");

    RaidTrapRevealResult nonTrapReveal = state.Reveal(steel.GlobalKey, RaidTrapRevealReason.Manual);
    Require(!nonTrapReveal.Revealed, "非陷阱揭示不应改变状态");
}

static void VerifyTrapClassificationUploadManifest()
{
    var package = new TrapClassificationUploadPackage(
        TrapClassificationUploadPackage.CurrentFormatVersion,
        DateTimeOffset.UnixEpoch,
        "admin-user",
        new[]
        {
            new TrapClassificationUploadEntry(
                "TrapSpike",
                "RimWorld.Building_TrapDamager",
                "ludeon.rimworld",
                "Core",
                InheritsBuildingTrap: true,
                CandidateRequiresApproval: false,
                "inherits:RimWorld.Building_Trap",
                AdminApproved: false),
            new TrapClassificationUploadEntry(
                "Milira_TrapRepulsive",
                "AncotLibrary.Building_TrapRepulsive",
                "milira.project",
                "Milira",
                InheritsBuildingTrap: false,
                CandidateRequiresApproval: true,
                "candidate:name-or-class-marker",
                AdminApproved: true),
            new TrapClassificationUploadEntry(
                "DecorativeMineSign",
                "SomeMod.DecorativeMineSign",
                "decor.mod",
                "Decor",
                InheritsBuildingTrap: false,
                CandidateRequiresApproval: true,
                "candidate:name-or-class-marker",
                AdminApproved: false)
        });

    Equal(1, package.AutoApprovedEntries.Count, "自动通过项数量");
    Equal(2, package.CandidateEntries.Count, "候选项数量");
    Equal(2, package.ApprovedManifestEntries.Count, "最终批准项数量");

    ThingDefTrapClassificationManifest manifest = ThingDefTrapClassificationManifestBuilder.FromUploadPackage(package);

    Require(manifest.EntriesByDefName.ContainsKey("TrapSpike"), "继承原版陷阱应进入批准清单");
    Require(manifest.EntriesByDefName.ContainsKey("Milira_TrapRepulsive"), "管理员确认的候选陷阱应进入批准清单");
    Require(!manifest.EntriesByDefName.ContainsKey("DecorativeMineSign"), "未确认候选项不应进入批准清单");
    Require(manifest.EntriesByDefName["TrapSpike"].InheritsBuildingTrap, "原版继承项应保留继承标记");
    Require(manifest.EntriesByDefName["Milira_TrapRepulsive"].ApprovedCustomTrap, "管理员确认候选项应标记为批准自定义陷阱");
}

static void VerifyRaidHiddenTrapList()
{
    ThingSummary vanillaTrap = ThingWithDef("trap-spike", "TrapSpike", "Building_TrapDamager") with
    {
        MapUniqueId = "0",
        GlobalKey = "owner:userA/colony:colonyA/snapshot:raid001/map:0/thing:trap-spike"
    };
    ThingSummary approvedModTrap = ThingWithDef("mod-trap", "Milira_TrapRepulsive", "AncotLibrary.Building_TrapRepulsive") with
    {
        MapUniqueId = "0",
        GlobalKey = "owner:userA/colony:colonyA/snapshot:raid001/map:0/thing:mod-trap"
    };
    ThingSummary unapprovedTrapLike = ThingWithDef("fake-trap", "DecorativeMineSign", "SomeMod.DecorativeMineSign") with
    {
        MapUniqueId = "0",
        GlobalKey = "owner:userA/colony:colonyA/snapshot:raid001/map:0/thing:fake-trap"
    };
    ThingSummary otherMapTrap = ThingWithDef("other-map-trap", "TrapSpike", "Building_TrapDamager") with
    {
        MapUniqueId = "1",
        GlobalKey = "owner:userA/colony:colonyA/snapshot:raid001/map:1/thing:other-map-trap"
    };
    ThingSummary pawn = ThingWithDef("pawn-colonist", "Human", "Pawn") with
    {
        MapUniqueId = "0",
        GlobalKey = "owner:userA/colony:colonyA/snapshot:raid001/map:0/thing:pawn-colonist",
        IsPawn = true
    };

    SaveSnapshotIndex snapshot = SnapshotWithThings(vanillaTrap, approvedModTrap, unapprovedTrapLike, otherMapTrap, pawn);
    var manifest = new ThingDefTrapClassificationManifest(new[]
    {
        new ThingDefTrapClassification("TrapSpike", "RimWorld.Building_TrapDamager", "Core", InheritsBuildingTrap: true, ApprovedCustomTrap: false),
        new ThingDefTrapClassification("Milira_TrapRepulsive", "AncotLibrary.Building_TrapRepulsive", "Milira", InheritsBuildingTrap: false, ApprovedCustomTrap: true)
    });

    RaidHiddenTrapList list = RaidHiddenTrapListBuilder.Build(
        "raid-event-001",
        new SnapshotIdentity("userA", "colonyA", "raid001"),
        snapshot,
        "0",
        manifest);

    Equal("raid-event-001", list.RaidEventId, "隐藏清单袭击事件");
    Equal("raid001", list.TargetSnapshot.SnapshotId, "隐藏清单目标快照");
    Equal("0", list.TargetMapUniqueId, "隐藏清单目标地图");
    Equal("Map_0", list.TargetClientMapLoadId, "客户端目标地图读取 ID");
    Equal(2, list.HiddenThings.Count, "具体隐藏对象数量");
    Require(list.HiddenThings.Any(thing => thing.LocalThingId == "trap-spike"), "原版陷阱应进入具体隐藏清单");
    Require(list.HiddenThings.Any(thing => thing.LocalThingId == "mod-trap"), "批准第三方陷阱应进入具体隐藏清单");
    Require(!list.HiddenThings.Any(thing => thing.LocalThingId == "fake-trap"), "未批准候选项不应进入具体隐藏清单");
    Require(!list.HiddenThings.Any(thing => thing.LocalThingId == "other-map-trap"), "其他地图陷阱不应进入当前地图隐藏清单");
    Require(!list.HiddenThings.Any(thing => thing.LocalThingId == "pawn-colonist"), "pawn 不应进入陷阱隐藏清单");
    Require(list.ClientHiddenThingKeys.Contains("trap-spike"), "客户端清单应包含本地 thingID");
    Require(list.ClientHiddenThingKeys.Contains("Thing_trap-spike"), "客户端清单应包含原版 UniqueLoadID");
}

static void VerifyRaidHiddenTrapDelivery()
{
    ThingSummary trap = ThingWithDef("trap-spike", "TrapSpike", "Building_TrapDamager") with
    {
        MapUniqueId = "0",
        GlobalKey = "owner:userA/colony:colonyA/snapshot:raid001/map:0/thing:trap-spike"
    };
    SaveSnapshotIndex snapshot = SnapshotWithThings(trap);
    var manifest = new ThingDefTrapClassificationManifest(new[]
    {
        new ThingDefTrapClassification("TrapSpike", "RimWorld.Building_TrapDamager", "Core", InheritsBuildingTrap: true, ApprovedCustomTrap: false)
    });
    RaidHiddenTrapList hiddenList = RaidHiddenTrapListBuilder.Build(
        "raid-event-001",
        new SnapshotIdentity("userA", "colonyA", "raid001"),
        snapshot,
        "0",
        manifest);

    RaidHiddenTrapDelivery delivery = RaidHiddenTrapDelivery.FromHiddenTrapList(hiddenList);

    Equal("raid-event-001", delivery.RaidEventId, "下发包袭击事件");
    Equal("raid001", delivery.TargetSnapshotId, "下发包目标快照");
    Equal("Map_0", delivery.TargetClientMapLoadId, "下发包客户端地图读取 ID");
    Require(delivery.HiddenThingKeys.Contains("trap-spike"), "下发包应包含本地 thingID");
    Require(delivery.HiddenThingKeys.Contains("Thing_trap-spike"), "下发包应包含 UniqueLoadID");
    Equal(2, delivery.HiddenThingKeys.Count, "下发包 key 去重后数量");
}

static void VerifyDisappearanceDiff()
{
    ThingSummary first = Thing("thing-a", stackCount: "1");
    ThingSummary second = Thing("thing-b", stackCount: "5");
    ThingSummary extra = Thing("thing-extra", stackCount: "99");

    RaidSettlementDiffResult diff = RaidSettlementDiffer.CompareByDisappearance(
        new[] { first, second },
        new[] { second, extra },
        new RaidSettlementPolicy(0.5, "event-a"));

    Equal(1, diff.StolenThingCount, "消失对象数量");
    Equal(1, diff.IgnoredExtraThingCount, "新增对象忽略数量");
    Equal(first.GlobalKey, diff.MissingThings[0].GlobalKey, "消失对象 key");
}

static void VerifyRaidSettlementReturnProcessing()
{
    SaveSnapshotPackage original = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "base-snapshot"),
        "original.rws",
        Thing("steel-stack", stackCount: "75") with
        {
            GlobalKey = "owner:defender/colony:colony-a/snapshot:base-snapshot/map:0/thing:steel-stack"
        },
        Thing("component-stack", stackCount: "12") with
        {
            GlobalKey = "owner:defender/colony:colony-a/snapshot:base-snapshot/map:0/thing:component-stack"
        });

    SaveSnapshotPackage returned = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "raid-return"),
        "returned.rws",
        Thing("projected-steel-stack", stackCount: "70") with
        {
            GlobalKey = "owner:defender/colony:colony-a/snapshot:raid-return/map:0/thing:projected-steel-stack",
            ClashOfRimOriginalThingId = "steel-stack"
        },
        Thing("extra-stack", stackCount: "99") with
        {
            GlobalKey = "owner:defender/colony:colony-a/snapshot:raid-return/map:0/thing:extra-stack"
        });

    var request = new RaidSettlementReturnRequest(
        "raid-event-001",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5);

    RaidSettlementReturnResult result = RaidSettlementReturnProcessor.Process(request);

    Require(result.Accepted, "合法返回快照应被接受");
    Require(result.Settlement is not null, "接受结果应包含结算");
    Equal(RaidSettlementReturnResultKind.Accepted, result.Kind, "结算结果类型");
    Equal(1, result.Settlement!.StolenThingCount, "整条消失对象数量");
    Equal(1, result.Settlement.ReducedStackThingCount, "堆叠减少对象数量");
    Equal(0, result.Settlement.IgnoredExtraThingCount, "没有原始远程 id 的返回物品不进入结算键集合");
    Equal(17, result.Settlement.TotalStolenStackCount, "总被抢数量");
    Equal(11, result.Settlement.TotalLossCount, "按上限后的总损失数量");
}

static void VerifyRaidSettlementReturnProcessingWithAttackerEvidenceMap()
{
    SaveSnapshotPackage original = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "base-snapshot"),
        "original.rws",
        Thing("steel-stack", stackCount: "75") with
        {
            MapUniqueId = "0",
            GlobalKey = "owner:defender/colony:colony-a/snapshot:base-snapshot/map:0/thing:steel-stack"
        },
        Thing("component-stack", stackCount: "12") with
        {
            MapUniqueId = "0",
            GlobalKey = "owner:defender/colony:colony-a/snapshot:base-snapshot/map:0/thing:component-stack"
        });

    SaveSnapshotPackage returned = SettlementPackage(
        new SnapshotIdentity("attacker", "colony-b", "attacker-evidence"),
        "returned.rws",
        Thing("projected-steel-stack", stackCount: "70") with
        {
            MapUniqueId = "9",
            GlobalKey = "owner:attacker/colony:colony-b/snapshot:attacker-evidence/map:9/thing:projected-steel-stack",
            ClashOfRimOriginalThingId = "steel-stack"
        },
        Thing("extra-stack", stackCount: "99") with
        {
            MapUniqueId = "9",
            GlobalKey = "owner:attacker/colony:colony-b/snapshot:attacker-evidence/map:9/thing:extra-stack"
        });

    RaidSettlementReturnResult result = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-event-attacker-evidence",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        ReturnedMapUniqueId: "9"));

    Require(result.Accepted, "攻击方普通证据快照应可用于结算");
    Equal(1, result.Settlement!.StolenThingCount, "整条消失对象数量");
    Equal(1, result.Settlement.ReducedStackThingCount, "堆叠减少对象数量");
    Equal(0, result.Settlement.IgnoredExtraThingCount, "证据快照新增对象没有原始远程 id 时不进入结算键集合");
    Equal(17, result.Settlement.TotalStolenStackCount, "总被抢数量");
    Equal(11, result.Settlement.TotalLossCount, "按比例上限后的损失数量");
}

static void VerifyRaidSettlementMatchesStableThingKeysWithoutOriginalThingId()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <zoneManager>
                  <allZones>
                    <li Class="Zone_Growing">
                      <cells>
                        <li>(3, 0, 3)</li>
                      </cells>
                    </li>
                  </allZones>
                </zoneManager>
                <things>
                  <thing Class="Building"><id>Wall4147</id><def>Wall</def><pos>(2, 0, 2)</pos><health>300</health></thing>
                  <thing Class="Plant"><id>Plant_Potato5000</id><def>Plant_Potato</def><pos>(3, 0, 3)</pos><health>85</health></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = XmlSettlementPackage(
        identity with { SnapshotId = "raid-return" },
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>9</uniqueID>
                <things>
                  <thing Class="Building"><id>ProjectedWall1</id><def>Wall</def><pos>(2, 0, 2)</pos><health>300</health></thing>
                  <thing Class="Plant"><id>ProjectedPlant1</id><def>Plant_Potato</def><pos>(3, 0, 3)</pos><health>85</health></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult result = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-event-stable-thing-key",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        1.0,
        ReturnedMapUniqueId: "9"));

    Require(result.Accepted, "缺少原始对象标记的返回快照仍应可结算");
    Equal(0, result.Settlement!.Losses.Count, "同位置同定义对象不应因 thing id 改写被误判为损失");
    Equal(0, result.Settlement.TotalLossCount, "稳定对象键匹配后不应产生损失");
}

static void VerifyRaidSettlementAggregatesDuplicateReturnedOriginalThingId()
{
    SaveSnapshotPackage original = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "base-snapshot"),
        "original.rws",
        Thing("steel-stack", stackCount: "75") with
        {
            MapUniqueId = "0",
            GlobalKey = "owner:defender/colony:colony-a/snapshot:base-snapshot/map:0/thing:steel-stack"
        });

    SaveSnapshotPackage returned = SettlementPackage(
        new SnapshotIdentity("attacker", "colony-b", "attacker-evidence"),
        "returned.rws",
        Thing("projected-steel-a", stackCount: "30") with
        {
            MapUniqueId = "9",
            GlobalKey = "owner:attacker/colony:colony-b/snapshot:attacker-evidence/map:9/thing:projected-steel-a",
            ClashOfRimOriginalThingId = "steel-stack"
        },
        Thing("projected-steel-b", stackCount: "40") with
        {
            MapUniqueId = "9",
            GlobalKey = "owner:attacker/colony:colony-b/snapshot:attacker-evidence/map:9/thing:projected-steel-b",
            ClashOfRimOriginalThingId = "steel-stack"
        });

    RaidSettlementReturnResult result = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-event-duplicate-returned-thing",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        ReturnedMapUniqueId: "9"));

    Require(result.Accepted, "重复原始对象映射应按同一原始对象聚合后结算");
    Equal(0, result.Settlement!.StolenThingCount, "聚合后对象仍存在，不应视为整组消失");
    Equal(1, result.Settlement.ReducedStackThingCount, "聚合后只应记录一条堆叠减少");
    Equal(5, result.Settlement.TotalStolenStackCount, "重复返回对象应聚合为剩余 70");
    Equal(5, result.Settlement.TotalLossCount, "比例上限只限制最大损失，不缩放实际小额损失");
}

static void VerifyRaidSettlementIgnoresPawns()
{
    ThingSummary defenderPawn = Thing("colonist", stackCount: "1") with
    {
        Class = "Pawn",
        IsPawn = true
    };
    ThingSummary attackerPawn = Thing("attacker", stackCount: "1") with
    {
        Class = "Pawn",
        IsPawn = true
    };
    SaveSnapshotPackage original = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "base-snapshot"),
        "original.rws",
        defenderPawn);
    SaveSnapshotPackage returned = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "raid-return"),
        "returned.rws",
        attackerPawn);

    RaidSettlementReturnResult result = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-event-ignore-pawns",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        1.0));

    Require(result.Accepted, "pawn 变化不应导致结算拒绝");
    Equal(0, result.Settlement!.Losses.Count, "pawn 消失和新增都不应生成损失");
    Equal(0, result.Settlement.IgnoredExtraThingCount, "新增 pawn 不应计入 extra thing");
}

static void VerifyRaidSettlementIgnoresPolicyDefs()
{
    ThingSummary ignoredMarker = ThingWithDef(
        "compat-marker",
        "ClashOfRim_TestCompatibilityMarker",
        "AIRsLight.ClashOfRim.Test.Marker");

    RaidSettlementDiffResult defaultDiff = RaidSettlementDiffer.CompareByDisappearance(
        new[] { ignoredMarker },
        Array.Empty<ThingSummary>(),
        new RaidSettlementPolicy(1.0, "default-policy"));
    Equal(1, defaultDiff.Losses.Count, "未配置忽略时自定义对象消失应参与结算");

    RaidSettlementDiffResult ignoredDiff = RaidSettlementDiffer.CompareByDisappearance(
        new[] { ignoredMarker },
        Array.Empty<ThingSummary>(),
        new RaidSettlementPolicy(
            1.0,
            "compat-policy",
            ignoredThingDefNames: new[] { "ClashOfRim_TestCompatibilityMarker" }));

    Equal(0, ignoredDiff.Losses.Count, "兼容包声明的忽略对象不应参与结算");
}

static void VerifyRaidSettlementIncludesGrowingZonePlants()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <zoneManager>
                  <allZones>
                    <li Class="Zone_Growing">
                      <ID>1</ID>
                      <baseLabel>Growing zone</baseLabel>
                      <cells>
                        <li>(1, 0, 1)</li>
                      </cells>
                      <plantDefToGrow>Plant_Potato</plantDefToGrow>
                    </li>
                  </allZones>
                </zoneManager>
                <things>
                  <thing Class="Plant"><id>crop-potato</id><def>Plant_Potato</def><pos>(1, 0, 1)</pos><health>85</health></thing>
                  <thing Class="Plant"><id>wild-bush</id><def>Plant_Brambles</def><pos>(2, 0, 2)</pos><health>50</health></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    Equal(1, original.Index.Maps.Single().GrowingZoneCells?.Count ?? 0, "应读取种植区格子数量");

    RaidSettlementReturnResult result = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-event-growing-zone-plants",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        1.0));

    Require(result.Accepted, "种植区植物结算应接受");
    Equal(1, result.Settlement!.Losses.Count, "只有种植区内植物应生成损失");
    Equal("crop-potato", result.Settlement.Losses.Single().Thing.LocalId, "种植区作物应纳入损失");
    Equal(1, result.Settlement.TotalLossCount, "种植区作物损失数量");
    Require(!result.Settlement.Losses.Any(loss => loss.Thing.LocalId == "wild-bush"), "野外植物不应纳入损失");
}

static void VerifyRaidSettlementSnapshotEditor()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="ThingWithComps"><id>steel-stack</id><def>Steel</def><stackCount>10</stackCount></thing>
                  <thing Class="ThingWithComps"><id>component-stack</id><def>ComponentIndustrial</def><stackCount>5</stackCount></thing>
                  <thing Class="Building_Bed"><id>Thing_Bed68282</id><def>Bed</def><stackCount>1</stackCount></thing>
                  <thing Class="Pawn">
                    <id>colonist</id>
                    <def>Human</def>
                    <kindDef>Colonist</kindDef>
                    <jobs>
                      <curJob>
                        <def>LayDown</def>
                        <loadID>70124</loadID>
                        <targetA>Thing_Bed68282</targetA>
                      </curJob>
                      <curDriver IsNull="True" />
                      <jobQueue><jobs /></jobQueue>
                      <formingCaravanTick>-1</formingCaravanTick>
                    </jobs>
                    <ownership>
                      <ownedBed>Thing_Bed68282</ownedBed>
                      <assignedMeditationSpot>null</assignedMeditationSpot>
                      <assignedGrave>null</assignedGrave>
                      <assignedThrone>null</assignedThrone>
                      <assignedDeathrestCasket>null</assignedDeathrestCasket>
                    </ownership>
                    <mindState>
                      <enemyTarget>Thing_Bed68282</enemyTarget>
                    </mindState>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws",
        Thing("projected-steel-stack", stackCount: "7") with
        {
            ClashOfRimOriginalThingId = "steel-stack"
        },
        Thing("attacker-pawn", stackCount: "1") with { Class = "Pawn", IsPawn = true });

    RaidSettlementReturnResult settlement = new(
        RaidSettlementReturnResultKind.Accepted,
        "raid-event-edit",
        original.Envelope.Identity,
        returned.Envelope.Identity,
        "0",
        new RaidSettlementDiffResult(
            MissingThings: Array.Empty<ThingSummary>(),
            Losses: new[]
            {
                new RaidSettlementLoss(Thing("steel-stack", "10"), 10, 7, 3, 3, 0, 0, 3, 3),
                new RaidSettlementLoss(Thing("component-stack", "5"), 5, null, 5, 5, 0, 0, 5, 5),
                new RaidSettlementLoss(Thing("Thing_Bed68282", "1"), 1, null, 1, 1, 0, 0, 1, 1)
            },
            IgnoredExtraThingCount: 0,
            LossRatio: 1.0));

    Require(settlement.Accepted, "结算应接受 XML 防守方快照");
    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-snapshot",
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    IReadOnlyList<XElement> things = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .ToList();
    XElement steel = things.Single(thing => thing.Element("id")?.Value == "steel-stack");
    Equal("7", steel.Element("stackCount")?.Value, "钢铁堆叠应按结算扣减");
    Require(things.All(thing => thing.Element("id")?.Value != "component-stack"), "整条损失物应从防守快照移除");
    Require(things.All(thing => thing.Element("id")?.Value != "Thing_Bed68282"), "被结算全损的床应从防守快照移除");
    Require(things.Any(thing => thing.Element("id")?.Value == "colonist"), "防守方 pawn 不应被结算编辑移除");
    Require(things.All(thing => thing.Element("id")?.Value != "attacker-pawn"), "战斗快照新增 pawn 不应进入编辑后的防守快照");
    XElement colonist = things.Single(thing => thing.Element("id")?.Value == "colonist");
    Equal("True", colonist.Element("jobs")?.Element("curJob")?.Attribute("IsNull")?.Value, "被删床上的睡眠 job 应被清空");
    Equal("null", colonist.Element("ownership")?.Element("ownedBed")?.Value, "被删床的所有权引用应被清空");
    Equal("null", colonist.Element("mindState")?.Element("enemyTarget")?.Value, "指向被删物的心智目标应被清空");
    Equal("edited-snapshot", edited.Envelope.Identity.SnapshotId, "编辑后快照 id");
    Equal("base-snapshot", edited.Envelope.PreviousSnapshotId, "编辑后快照应连接原防守快照");
}

static void VerifyRaidSettlementSnapshotEditorAddsBattlefieldResidues()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="ThingWithComps"><id>steel-stack</id><def>Steel</def><stackCount>10</stackCount></thing>
                  <thing Class="Filth"><id>Filth_Blood100</id><def>Filth_Blood</def><pos>(1, 0, 1)</pos><disappearAfterTicks>1000</disappearAfterTicks></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = XmlSettlementPackage(
        identity with { SnapshotId = "raid-return" },
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="ThingWithComps"><id>projected-steel-stack</id><def>Steel</def><stackCount>10</stackCount><clashOfRimOriginalThingId>steel-stack</clashOfRimOriginalThingId></thing>
                  <thing Class="Filth"><id>Filth_Blood100</id><def>Filth_Blood</def><pos>(1, 0, 1)</pos><disappearAfterTicks>1000</disappearAfterTicks></thing>
                  <thing Class="Filth"><id>Filth_Blood101</id><def>Filth_Blood</def><pos>(2, 0, 2)</pos><thickness>3</thickness><disappearAfterTicks>2000</disappearAfterTicks><sources><li>Pawn_123</li></sources></thing>
                  <thing Class="Filth"><id>Filth_Ash102</id><def>Filth_Ash</def><pos>(3, 0, 3)</pos><disappearAfterTicks>3000</disappearAfterTicks></thing>
                  <thing Class="Filth"><id>SandbagRubble103</id><def>SandbagRubble</def><pos>(4, 0, 4)</pos><disappearAfterTicks>4000</disappearAfterTicks></thing>
                  <thing Class="Building"><id>Sandbags104</id><def>Sandbags</def><pos>(5, 0, 5)</pos><health>300</health></thing>
                  <thing Class="ThingWithComps"><id>silver-extra</id><def>Silver</def><pos>(6, 0, 6)</pos><stackCount>99</stackCount></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-event-residue",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        1.0));

    Require(settlement.Accepted, "带战场覆盖层的结算应接受");
    Equal(3, settlement.Settlement!.BattlefieldResidues.Count, "只应收集新增覆盖层");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-residue-snapshot",
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    IReadOnlyList<XElement> things = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .ToList();
    Require(things.Any(thing => ThingDef(thing) == "Filth_Blood" && ThingPos(thing) == "(2, 0, 2)"), "新增血迹应写入防守快照");
    Require(things.Any(thing => ThingDef(thing) == "Filth_Ash" && ThingPos(thing) == "(3, 0, 3)"), "新增灰烬应写入防守快照");
    Require(things.Any(thing => ThingDef(thing) == "SandbagRubble" && ThingPos(thing) == "(4, 0, 4)"), "新增沙袋碎屑应写入防守快照");
    Equal(1, things.Count(thing => ThingDef(thing) == "Filth_Blood" && ThingPos(thing) == "(1, 0, 1)"), "原有血迹不应被重复复制");
    Require(!things.Any(thing => ThingDef(thing) == "Sandbags"), "真实沙袋建筑不应作为战场覆盖层复制");
    Require(!things.Any(thing => ThingDef(thing) == "Silver"), "普通新增物品不应作为战场覆盖层复制");

    XElement copiedBlood = things.Single(thing => ThingDef(thing) == "Filth_Blood" && ThingPos(thing) == "(2, 0, 2)");
    Require(copiedBlood.Element("sources") is null, "血迹来源引用不应复制");
    Equal("3", copiedBlood.Element("thickness")?.Value, "血迹厚度应保留");
    Equal("2000", copiedBlood.Element("disappearAfterTicks")?.Value, "血迹消失时间应保留");
    Require(copiedBlood.Element("id")?.Value.StartsWith("Filth_Blood", StringComparison.Ordinal) == true, "复制血迹应生成合法 thing id");

    IReadOnlyList<int> copiedResidueIdNumbers = things
        .Where(thing =>
            (ThingDef(thing) == "Filth_Blood" && ThingPos(thing) == "(2, 0, 2)")
            || (ThingDef(thing) == "Filth_Ash" && ThingPos(thing) == "(3, 0, 3)")
            || (ThingDef(thing) == "SandbagRubble" && ThingPos(thing) == "(4, 0, 4)"))
        .Select(ThingIdNumber)
        .ToList();
    Equal(copiedResidueIdNumbers.Count, copiedResidueIdNumbers.Distinct().Count(), "复制覆盖层 thing id 数字后缀不能重复");
}

static void VerifyAdaptiveStorageSaveIndexExtension()
{
    XElement containerElement = XElement.Parse("""
        <li Class="AdaptiveStorage.ThingClass">
          <id>storage-1</id>
          <def>AdaptiveShelf</def>
          <pos>(10, 0, 20)</pos>
          <StoredThings>
            <Things>
              <li Class="ThingWithComps">
                <id>steel-a</id>
                <def>Steel</def>
                <stackCount>75</stackCount>
                <stuff>Metallic</stuff>
              </li>
            </Things>
          </StoredThings>
        </li>
        """);
    ThingSummary container = ThingWithDef("storage-1", "AdaptiveShelf", "AdaptiveStorage.ThingClass") with
    {
        Position = "(10, 0, 20)"
    };
    var extension = new AdaptiveStorageSaveIndexExtension();

    IReadOnlyList<ThingSummary> withoutMod = extension.ReadContainedThings(
        containerElement,
        container,
        ThirdPartyContext(Array.Empty<string>())).ToList();
    Equal(0, withoutMod.Count, "未加载 Adaptive Storage 时不应解析容器内容");

    IReadOnlyList<ThingSummary> withMod = extension.ReadContainedThings(
        containerElement,
        container,
        ThirdPartyContext(new[] { "adaptive.storage.framework" })).ToList();
    Equal(1, withMod.Count, "Adaptive Storage 容器内容数量");
    Equal("Steel", withMod[0].Def, "Adaptive Storage 容器内容 def");
    Equal(container.GlobalKey, withMod[0].ContainerGlobalKey, "Adaptive Storage 容器全局键");
}

static void VerifyVehicleFrameworkSaveIndexExtension()
{
    XElement vehicleElement = XElement.Parse("""
        <li Class="Vehicles.VehiclePawn">
          <id>vehicle-1</id>
          <def>VF_TestVehicle</def>
          <kindDef>VF_TestVehicleKind</kindDef>
          <pos>(30, 0, 40)</pos>
          <statHandler>
            <components>
              <li><health>50</health></li>
              <li><health>150</health></li>
            </components>
          </statHandler>
          <inventory>
            <innerContainer>
              <innerList>
                <li Class="ThingWithComps">
                  <id>component-a</id>
                  <clashOfRimOriginalThingId>component-original</clashOfRimOriginalThingId>
                  <def>ComponentIndustrial</def>
                  <stackCount>3</stackCount>
                </li>
              </innerList>
            </innerContainer>
          </inventory>
          <handlers>
            <li>
              <thingOwner>
                <innerList>
                  <li Class="Pawn">
                    <id>passenger-a</id>
                    <clashOfRimOriginalThingId>passenger-original</clashOfRimOriginalThingId>
                    <def>Human</def>
                    <kindDef>Colonist</kindDef>
                    <faction>Faction_0</faction>
                  </li>
                </innerList>
              </thingOwner>
            </li>
          </handlers>
        </li>
        """);
    ThingSummary vehicle = ThingWithDef("vehicle-1", "VF_TestVehicle", "Vehicles.VehiclePawn") with
    {
        Position = "(30, 0, 40)",
        ClashOfRimOriginalThingId = "vehicle-original"
    };
    var extension = new VehicleFrameworkSaveIndexExtension();

    IReadOnlyList<ThingSummary> withoutMod = extension.ReadContainedThings(
        vehicleElement,
        vehicle,
        ThirdPartyContext(Array.Empty<string>())).ToList();
    Equal(0, withoutMod.Count, "未加载 Vehicle Framework 时不应解析载具内容");

    IReadOnlyList<ThingSummary> withMod = extension.ReadContainedThings(
        vehicleElement,
        vehicle,
        ThirdPartyContext(new[] { "SmashPhil.VehicleFramework" })).ToList();
    Equal(3, withMod.Count, "Vehicle Framework 载具内容数量");
    ThingSummary asset = withMod.Single(thing => thing.SettlementAssetKind == VehicleFrameworkSaveIndexExtension.VehicleSettlementAssetKind);
    Equal("vehicle-1", asset.LocalId, "载具结算资产应绑定真实载具 id");
    Equal("200", asset.HitPoints, "载具结算资产应汇总组件当前生命值");
    Require(!asset.IsPawn && asset.SettlementDamageOnly, "载具结算资产应作为只损血资产");
    ThingSummary cargo = withMod.Single(thing => thing.Def == "ComponentIndustrial");
    Equal(vehicle.GlobalKey, cargo.ContainerGlobalKey, "载具货物应绑定容器");
    Equal("vehicle-original/vehicle-cargo:component-original", cargo.ClashOfRimOriginalThingId, "载具货物应恢复复合原始ID");
    Equal(VehicleFrameworkSaveIndexExtension.VehicleCargoSettlementAssetKind, cargo.SettlementAssetKind, "载具货物应标记为载具库存结算资产");
    ThingSummary passenger = withMod.Single(thing => thing.Def == "Human");
    Require(passenger.IsPawn, "载具乘员应作为 pawn 内容索引");
    Equal(vehicle.GlobalKey, passenger.ContainerGlobalKey, "载具乘员应绑定容器");
    Equal("vehicle-original/vehicle-passenger:passenger-original", passenger.ClashOfRimOriginalThingId, "载具乘员应恢复复合原始ID");
}

static void VerifyVehicleFrameworkCargoOriginalIdsSettle()
{
    SaveIndexExtensionRegistry.Register(new VehicleFrameworkSaveIndexExtension());

    var defenderIdentity = new SnapshotIdentity("defender", "colony-ddd", "defender-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        defenderIdentity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Vehicles.VehiclePawn">
                    <id>VVE_Roadkill39914</id>
                    <def>VVE_Roadkill</def>
                    <kindDef>VVE_Roadkill</kindDef>
                    <pos>(30, 0, 40)</pos>
                    <statHandler>
                      <components>
                        <li><health>100</health></li>
                      </components>
                    </statHandler>
                    <inventory>
                      <innerContainer>
                        <innerList>
                          <li Class="ThingWithComps">
                            <id>Steel39917</id>
                            <def>Steel</def>
                            <stackCount>70</stackCount>
                          </li>
                        </innerList>
                      </innerContainer>
                    </inventory>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    SaveSnapshotPackage returned = XmlSettlementPackage(
        new SnapshotIdentity("attacker", "colony-a", "returned-snapshot"),
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <maps>
              <li>
                <uniqueID>raid-map</uniqueID>
                <things>
                  <thing Class="Vehicles.VehiclePawn">
                    <id>VVE_Roadkill90001</id>
                    <clashOfRimOriginalThingId>VVE_Roadkill39914</clashOfRimOriginalThingId>
                    <def>VVE_Roadkill</def>
                    <kindDef>VVE_Roadkill</kindDef>
                    <pos>(30, 0, 40)</pos>
                    <statHandler>
                      <components>
                        <li><health>100</health></li>
                      </components>
                    </statHandler>
                    <inventory>
                      <innerContainer>
                        <innerList>
                          <li Class="ThingWithComps">
                            <id>Steel90002</id>
                            <clashOfRimOriginalThingId>Steel39917</clashOfRimOriginalThingId>
                            <def>Steel</def>
                            <stackCount>70</stackCount>
                          </li>
                        </innerList>
                      </innerContainer>
                    </inventory>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-vehicle-cargo-original-id",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        ReturnedMapUniqueId: "raid-map"));

    Require(settlement.Accepted, "载具货物原始ID结算样本应被接受");
    Require(!settlement.Settlement!.Losses.Any(loss => string.Equals(loss.Thing.Def, "Steel", StringComparison.Ordinal)),
        "仍在载具内的钢铁不应因投影ID变化被误判损失");
}

static void VerifyVehicleFrameworkDestroyedVehicleCargoSettlesAsFullLoss()
{
    SaveIndexExtensionRegistry.Register(new VehicleFrameworkSaveIndexExtension());

    var defenderIdentity = new SnapshotIdentity("defender", "colony-ddd", "defender-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        defenderIdentity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>defender-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Vehicles.VehiclePawn">
                    <id>VVE_Roadkill39914</id>
                    <def>VVE_Roadkill</def>
                    <kindDef>VVE_Roadkill</kindDef>
                    <pos>(30, 0, 40)</pos>
                    <statHandler>
                      <components>
                        <li><health>50</health></li>
                        <li><health>50</health></li>
                      </components>
                    </statHandler>
                    <inventory>
                      <innerContainer>
                        <innerList>
                          <li Class="ThingWithComps">
                            <id>Steel39917</id>
                            <def>Steel</def>
                            <stackCount>70</stackCount>
                          </li>
                        </innerList>
                      </innerContainer>
                    </inventory>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    SaveSnapshotPackage returned = XmlSettlementPackage(
        new SnapshotIdentity("attacker", "colony-a", "returned-snapshot"),
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <maps>
              <li>
                <uniqueID>raid-map</uniqueID>
                <things />
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-vehicle-destroyed-cargo-loss",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        ReturnedMapUniqueId: "raid-map",
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["VVE_Roadkill"] = 100
        },
        MinimumRemainingHitPointsRatio: 0.1,
        BuildingHitPointsLossRatio: 0.9));

    Require(settlement.Accepted, "载具消失证据应被接受");
    RaidSettlementLoss cargoLoss = settlement.Settlement!.Losses.Single(loss => string.Equals(loss.Thing.Def, "Steel", StringComparison.Ordinal));
    Equal(70, cargoLoss.StolenStackCount, "载具被摧毁时库存全部视为损失");
    Equal(70, cargoLoss.LossCount, "载具被摧毁时库存损失不再套普通物品比例");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-vehicle-destroyed-cargo-loss",
        DateTimeOffset.UnixEpoch.AddMinutes(3),
        new IRaidSettlementSnapshotEditorExtension[]
        {
            new VehicleFrameworkRaidSettlementSnapshotEditorExtension()
        });

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement vehicle = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "VVE_Roadkill39914");
    Require(!vehicle
        .Element("inventory")!
        .Element("innerContainer")!
        .Element("innerList")!
        .Elements("li")
        .Any(), "载具被摧毁时原存档载具库存应被清空");
    IReadOnlyList<string> componentHealths = vehicle
        .Element("statHandler")!
        .Element("components")!
        .Elements("li")
        .Select(component => component.Element("health")?.Value ?? string.Empty)
        .ToList();
    Equal("5", componentHealths[0], "载具本体仍按生命值保护规则降低组件生命");
    Equal("5", componentHealths[1], "载具本体仍按生命值保护规则降低组件生命");
}

static void VerifyVehicleFrameworkSettlementComponentDamage()
{
    SaveIndexExtensionRegistry.Register(new VehicleFrameworkSaveIndexExtension());

    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Vehicles.VehiclePawn">
                    <id>vehicle-1</id>
                    <def>VF_TestVehicle</def>
                    <kindDef>VF_TestVehicleKind</kindDef>
                    <pos>(30, 0, 40)</pos>
                    <statHandler>
                      <components>
                        <li><health>50</health></li>
                        <li><health>150</health></li>
                      </components>
                    </statHandler>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    ThingSummary vehicleAsset = original.Index.Things.Single(thing =>
        thing.SettlementAssetKind == VehicleFrameworkSaveIndexExtension.VehicleSettlementAssetKind);
    Equal("200", vehicleAsset.HitPoints, "载具索引应汇总原始组件生命");

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-vehicle-component-damage",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        1.0,
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["VF_TestVehicle"] = 200
        },
        MinimumRemainingHitPointsRatio: 0.1,
        BuildingHitPointsLossRatio: 0.9));

    Require(settlement.Accepted, "载具消失证据应按只损血资产结算");
    RaidSettlementLoss loss = settlement.Settlement!.Losses.Single();
    Equal(0, loss.LossCount, "载具不应被结算删除");
    Equal(20, loss.RemainingHitPointsAfterDamage, "载具应按建筑生命保护线降低总组件生命");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-vehicle-component-damage",
        DateTimeOffset.UnixEpoch.AddMinutes(3),
        new IRaidSettlementSnapshotEditorExtension[]
        {
            new VehicleFrameworkRaidSettlementSnapshotEditorExtension()
        });

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement vehicle = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "vehicle-1");
    IReadOnlyList<string> componentHealths = vehicle
        .Element("statHandler")!
        .Element("components")!
        .Elements("li")
        .Select(component => component.Element("health")?.Value ?? string.Empty)
        .ToList();
    Equal(2, componentHealths.Count, "载具组件数量应保持不变");
    Equal("5", componentHealths[0], "第一个组件应按比例降低生命值");
    Equal("15", componentHealths[1], "第二个组件应按比例降低生命值");
}

static void VerifyVehicleFrameworkExistingComponentDamage()
{
    SaveIndexExtensionRegistry.Register(new VehicleFrameworkSaveIndexExtension());

    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Vehicles.VehiclePawn">
                    <id>vehicle-1</id>
                    <def>VF_TestVehicle</def>
                    <kindDef>VF_TestVehicleKind</kindDef>
                    <pos>(30, 0, 40)</pos>
                    <statHandler>
                      <components>
                        <li><health>50</health></li>
                        <li><health>150</health></li>
                      </components>
                    </statHandler>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = XmlSettlementPackage(
        identity with { SnapshotId = "raid-return" },
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta>
            <gameVersion>1.6-test</gameVersion>
            <modIds>
              <li>SmashPhil.VehicleFramework</li>
            </modIds>
          </meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Vehicles.VehiclePawn">
                    <id>vehicle-raid-copy</id>
                    <clashOfRimOriginalThingId>vehicle-1</clashOfRimOriginalThingId>
                    <def>VF_TestVehicle</def>
                    <kindDef>VF_TestVehicleKind</kindDef>
                    <pos>(30, 0, 40)</pos>
                    <statHandler>
                      <components>
                        <li><health>30</health></li>
                        <li><health>90</health></li>
                      </components>
                    </statHandler>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-vehicle-existing-component-damage",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        1.0,
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["VF_TestVehicle"] = 200
        },
        MinimumRemainingHitPointsRatio: 0.1,
        BuildingHitPointsLossRatio: 0.9));

    Require(settlement.Accepted, "载具仍存在时应按组件生命差异结算");
    RaidSettlementLoss loss = settlement.Settlement!.Losses.Single();
    Equal(0, loss.LossCount, "受伤载具不应被结算删除");
    Equal(120, loss.RemainingHitPointsAfterDamage, "仍存在的载具应保留战斗后的组件总生命");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-vehicle-existing-component-damage",
        DateTimeOffset.UnixEpoch.AddMinutes(3),
        new IRaidSettlementSnapshotEditorExtension[]
        {
            new VehicleFrameworkRaidSettlementSnapshotEditorExtension()
        });

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    IReadOnlyList<string> componentHealths = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "vehicle-1")
        .Element("statHandler")!
        .Element("components")!
        .Elements("li")
        .Select(component => component.Element("health")?.Value ?? string.Empty)
        .ToList();
    Equal("30", componentHealths[0], "第一个组件应保留战斗后生命");
    Equal("90", componentHealths[1], "第二个组件应保留战斗后生命");
}

static void VerifyPackableBuildingSettlementDamage()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Bed">
                    <id>bed-a</id>
                    <def>Bed</def>
                    <stuff>Steel</stuff>
                    <health>100</health>
                    <comps>
                      <li Class="CompQuality"><quality>Excellent</quality></li>
                    </comps>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");
    ThingSummary indexedBed = original.Index.Things.Single(thing => thing.LocalId == "bed-a");
    Equal("Steel", indexedBed.Stuff, "可打包建筑材料应从快照读取");
    Equal("Excellent", indexedBed.Quality, "可打包建筑品质应从快照读取");

    RaidSettlementReturnResult? settlement = null;
    for (int index = 0; index < 1000; index++)
    {
        RaidSettlementReturnResult candidate = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
            $"raid-packable-damage-{index}",
            original.Envelope.Identity,
            original,
            returned,
            "0",
            0.5,
            new[] { "Bed" },
            BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Bed"] = 100 }));
        RaidSettlementLoss? loss = candidate.Settlement?.Losses.SingleOrDefault();
        if (candidate.Accepted && loss?.LossCount == 0 && loss.RemainingHitPointsAfterDamage.HasValue)
        {
            settlement = candidate;
            break;
        }
    }

    Require(settlement is not null, "应能找到可打包建筑未全损的确定性样本");
    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement!,
        "edited-packable-damage",
        DateTimeOffset.UnixEpoch.AddMinutes(2));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement bed = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "bed-a");
    Equal("10", bed.Element("health")?.Value, "未全损的可打包建筑应按建筑耐久损失比例降低生命值");
}

static void VerifyPackableBuildingMinimumRemainingHitPointsRatio()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Bed">
                    <id>bed-a</id>
                    <def>Bed</def>
                    <stuff>Steel</stuff>
                    <health>100</health>
                    <comps>
                      <li Class="CompQuality"><quality>Excellent</quality></li>
                    </comps>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    RaidSettlementReturnResult? settlement = null;
    for (int index = 0; index < 1000; index++)
    {
        RaidSettlementReturnResult candidate = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
            $"raid-packable-minimum-{index}",
            original.Envelope.Identity,
            original,
            returned,
            "0",
            0.5,
            new[] { "Bed" },
            BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Bed"] = 100 },
            StuffHitPointFactorByDefName: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Steel"] = 2f },
            MinimumRemainingHitPointsRatio: 0.4,
            BuildingHitPointsLossRatio: 0.8));
        RaidSettlementLoss? loss = candidate.Settlement?.Losses.SingleOrDefault();
        if (candidate.Accepted && loss?.LossCount == 0 && loss.RemainingHitPointsAfterDamage.HasValue)
        {
            settlement = candidate;
            break;
        }
    }

    Require(settlement is not null, "应能找到可打包建筑未全损的最低生命比例样本");
    Equal(80, settlement!.Settlement!.Losses.Single().RemainingHitPointsAfterDamage, "最低剩余生命应按估算最大生命比例截断");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement!,
        "edited-packable-minimum",
        DateTimeOffset.UnixEpoch.AddMinutes(2));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement bed = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "bed-a");
    Equal("80", bed.Element("health")?.Value, "编辑后的建筑生命应保留最低比例");
}

static void VerifyNonPackableBuildingSettlementDamage()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Wall">
                    <id>wall-a</id>
                    <def>Wall</def>
                    <stuff>Steel</stuff>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-non-packable-damage",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        PackableBuildingDefNames: Array.Empty<string>(),
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Wall"] = 100 },
        MinimumRemainingHitPointsRatio: 0.1));

    Require(settlement.Accepted, "不可打包建筑消失应能结算");
    RaidSettlementLoss loss = settlement.Settlement!.Losses.Single();
    Equal(0, loss.LossCount, "不可打包建筑不应被全损删除");
    Equal(10, loss.RemainingHitPointsAfterDamage, "不可打包建筑应按建筑耐久损失比例降低生命值");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-non-packable-damage",
        DateTimeOffset.UnixEpoch.AddMinutes(3));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement wall = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "wall-a");
    Equal("10", wall.Element("health")?.Value, "编辑后不可打包建筑应仍存在并降低生命值");
}

static void VerifyUnknownBuildingWithoutHitPointBaselineIsSkipped()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Wall">
                    <id>unknown-building-a</id>
                    <def>UnknownBuilding</def>
                    <stuff>WoodLog</stuff>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-unknown-building-skipped",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        PackableBuildingDefNames: Array.Empty<string>(),
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Wall"] = 100 },
        MinimumRemainingHitPointsRatio: 0.1));

    Require(settlement.Accepted, "未知建筑不应导致结算拒绝");
    Equal(0, settlement.Settlement!.Losses.Count, "缺少最大生命值基线的建筑不应作为物品全损结算");
}

static void VerifyHardProtectedSettlementThings()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="ThingWithComps">
                    <id>gravcore-a</id>
                    <def>Gravcore</def>
                    <pos>(12, 0, 12)</pos>
                    <stackCount>1</stackCount>
                  </thing>
                  <thing Class="Building">
                    <id>void-monolith-a</id>
                    <def>VoidMonolith</def>
                    <pos>(14, 0, 14)</pos>
                    <health>300</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-hard-protected-things",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        LossRatio: 1.0,
        PackableBuildingDefNames: Array.Empty<string>(),
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["VoidMonolith"] = 300 },
        MinimumRemainingHitPointsRatio: 0.1));

    Require(settlement.Accepted, "硬保护对象不应导致结算拒绝");
    Equal(0, settlement.Settlement!.Losses.Count, "逆重核心和虚空巨石不应作为袭击损失处理");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-hard-protected-things",
        DateTimeOffset.UnixEpoch.AddMinutes(5));
    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    List<string> remainingDefs = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Select(thing => thing.Element("def")?.Value ?? string.Empty)
        .ToList();
    Require(remainingDefs.Contains("Gravcore"), "编辑后逆重核心应保留在防守方快照中");
    Require(remainingDefs.Contains("VoidMonolith"), "编辑后虚空巨石应保留在防守方快照中");
}

static void VerifyOrdinaryRaidEvidenceMissingOriginalIdKeepsNonPackableBuilding()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Wall">
                    <id>Wall4147</id>
                    <def>Wall</def>
                    <pos>(2, 0, 2)</pos>
                    <stuff>WoodLog</stuff>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = XmlSettlementPackage(
        identity with { SnapshotId = "raid-return" },
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>9</uniqueID>
                <things />
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-ordinary-evidence-missing-original-id",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        PackableBuildingDefNames: Array.Empty<string>(),
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Wall"] = 100 },
        MinimumRemainingHitPointsRatio: 0.1,
        ReturnedMapUniqueId: "9"));

    Require(settlement.Accepted, "攻击方普通保存证据应能结算");
    RaidSettlementLoss loss = settlement.Settlement!.Losses.Single();
    Equal(0, loss.LossCount, "缺少原始 ID 的普通保存证据不应让不可打包建筑全损");
    Equal(10, loss.RemainingHitPointsAfterDamage, "缺少原始 ID 时已知不可打包建筑也应只降低生命值");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-ordinary-evidence-missing-original-id",
        DateTimeOffset.UnixEpoch.AddMinutes(4));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement wall = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "Wall4147");
    Equal("10", wall.Element("health")?.Value, "普通保存离线结算编辑后墙应保留并降耐久");
}

static void VerifyRaidTrapSettlementFullLoss()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Trap">
                    <id>trap-a</id>
                    <def>TrapIED_HighExplosive</def>
                    <pos>(10, 0, 10)</pos>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = SettlementPackage(
        identity with { SnapshotId = "raid-return" },
        "returned.rws");

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-trap-full-loss",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        LossRatio: 0,
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["TrapIED_HighExplosive"] = 100 },
        MinimumRemainingHitPointsRatio: 0.9,
        TrapDefNames: new[] { "TrapIED_HighExplosive" }));

    Require(settlement.Accepted, "地雷消失应能结算");
    RaidSettlementLoss loss = settlement.Settlement!.Losses.Single();
    Equal(1, loss.LossCount, "地雷应绕过比例上限按实际消失全损");
    Equal(null, loss.RemainingHitPointsAfterDamage, "地雷全损不应转成建筑耐久损失");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-trap-full-loss",
        DateTimeOffset.UnixEpoch.AddMinutes(5));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    int remainingThings = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Count();
    Equal(0, remainingThings, "编辑后触发或被摧毁的地雷应直接消失");
}

static void VerifyUntriggeredHiddenTrapProxySettlementIdentity()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Trap">
                    <id>trap-a</id>
                    <def>TrapIED_HighExplosive</def>
                    <pos>(10, 0, 10)</pos>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = XmlSettlementPackage(
        identity with { SnapshotId = "raid-return" },
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="AIRsLight.ClashOfRim.Raids.Building_ClashHiddenTrapProxy">
                    <id>ClashOfRim_HiddenTrapProxy123</id>
                    <def>ClashOfRim_HiddenTrapProxy</def>
                    <pos>(10, 0, 10)</pos>
                    <health>100</health>
                    <clashOfRimOriginalTrapId>trap-a</clashOfRimOriginalTrapId>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-trap-proxy",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        LossRatio: 0,
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["TrapIED_HighExplosive"] = 100 },
        MinimumRemainingHitPointsRatio: 0.9,
        TrapDefNames: new[] { "TrapIED_HighExplosive" }));

    Require(settlement.Accepted, "未触发隐藏地雷代理应能结算");
    Equal(0, settlement.Settlement!.Losses.Count, "带原始地雷 id 的代理不应被误判为地雷消失");
}

static void VerifyExistingBuildingSettlementDamage()
{
    var identity = new SnapshotIdentity("defender", "colony-a", "base-snapshot");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>base-snapshot</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
              </li>
            </components>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Wall">
                    <id>wall-a</id>
                    <def>Wall</def>
                    <stuff>Steel</stuff>
                    <pos>(10, 0, 10)</pos>
                    <health>100</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);
    SaveSnapshotPackage returned = XmlSettlementPackage(
        identity with { SnapshotId = "raid-return" },
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <things>
                  <thing Class="Building_Wall">
                    <id>remote-wall-a</id>
                    <def>Wall</def>
                    <stuff>Steel</stuff>
                    <pos>(10, 0, 10)</pos>
                    <health>30</health>
                  </thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest(
        "raid-existing-building-damage",
        original.Envelope.Identity,
        original,
        returned,
        "0",
        0.5,
        PackableBuildingDefNames: Array.Empty<string>(),
        BuildingMaxHitPointsByDefName: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Wall"] = 100 },
        MinimumRemainingHitPointsRatio: 0.1));

    Require(settlement.Accepted, "仍存在的受伤建筑应能结算");
    Equal(0, settlement.Settlement!.StolenThingCount, "受伤建筑不应视为整件消失");
    Equal(0, settlement.Settlement.ReducedStackThingCount, "受伤建筑不应计入堆叠减少");
    RaidSettlementLoss loss = settlement.Settlement.Losses.Single();
    Equal(0, loss.LossCount, "受伤建筑不应被删除");
    Equal(30, loss.RemainingHitPointsAfterDamage, "受伤建筑应保留战斗后的耐久，但不能低于建筑保护线");

    SaveSnapshotPackage edited = RaidSettlementSnapshotEditor.ApplySettlementLosses(
        original,
        settlement,
        "edited-existing-building-damage",
        DateTimeOffset.UnixEpoch.AddMinutes(4));

    XDocument editedDocument;
    using (var stream = new MemoryStream(edited.Payload))
    {
        editedDocument = XDocument.Load(stream);
    }

    XElement wall = editedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .Single()
        .Element("things")!
        .Elements("thing")
        .Single(thing => thing.Element("id")?.Value == "wall-a");
    Equal("30", wall.Element("health")?.Value, "编辑后仍存在的受伤建筑应保留耐久损失");
}

static void VerifyRaidSettlementReturnRejections()
{
    SaveSnapshotPackage original = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "base-snapshot"),
        "original.rws",
        Thing("steel-stack", stackCount: "75"));
    SaveSnapshotPackage returned = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "raid-return"),
        "returned.rws",
        Thing("steel-stack", stackCount: "70"));
    SaveSnapshotPackage wrongColony = SettlementPackage(
        new SnapshotIdentity("other", "colony-b", "raid-return"),
        "wrong.rws",
        Thing("steel-stack", stackCount: "70"));
    SaveSnapshotPackage missingMap = SettlementPackage(
        new SnapshotIdentity("defender", "colony-a", "raid-return"),
        "missing-map.rws",
        Thing("steel-stack", stackCount: "70") with { MapUniqueId = "other-map" });

    Equal(
        RaidSettlementReturnResultKind.MissingEventId,
        RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest("", original.Envelope.Identity, original, returned, "0", 0.5)).Kind,
        "缺少事件 ID 应拒绝");

    Equal(
        RaidSettlementReturnResultKind.OriginalSnapshotIdentityMismatch,
        RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest("raid-event", original.Envelope.Identity with { SnapshotId = "wrong" }, original, returned, "0", 0.5)).Kind,
        "原始快照身份不匹配应拒绝");

    Equal(
        RaidSettlementReturnResultKind.ReturnedSnapshotColonyMismatch,
        RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest("raid-event", original.Envelope.Identity, original, wrongColony, "0", 0.5)).Kind,
        "返回快照殖民地不匹配应拒绝");

    Equal(
        RaidSettlementReturnResultKind.TargetMapNotFoundInReturnedSnapshot,
        RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest("raid-event", original.Envelope.Identity, original, missingMap, "0", 0.5)).Kind,
        "返回快照缺少目标地图应拒绝");

    Equal(
        RaidSettlementReturnResultKind.InvalidLossRatio,
        RaidSettlementReturnProcessor.Process(new RaidSettlementReturnRequest("raid-event", original.Envelope.Identity, original, returned, "0", 1.5)).Kind,
        "损失比例无效应拒绝");
}

static void VerifyRaidAttackerSnapshotCleanupOnlyRemovesRaidBattleMap()
{
    var identity = new SnapshotIdentity("attacker", "colony-a", "attacker-before-timeout");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <currentMapIndex>1</currentMapIndex>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>attacker-before-timeout</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
                <clashOfRimActiveRaidBattleSession>
                  <eventId>raid-timeout-001</eventId>
                  <attackPawnThingIds>
                    <li>raid-attacker</li>
                    <li>escaped-attacker</li>
                  </attackPawnThingIds>
                </clashOfRimActiveRaidBattleSession>
                <clashOfRimActiveSupportAssignments>
                  <li>
                    <eventId>support-in-raid</eventId>
                    <pawnThingId>raid-support</pawnThingId>
                    <pawnGlobalKey>owner:ally/colony:ally-colony/snapshot:support/map:caravan/thing:raid-support</pawnGlobalKey>
                    <pawnLabel>支援者</pawnLabel>
                    <autoReturnOnSettlement>True</autoReturnOnSettlement>
                  </li>
                  <li>
                    <eventId>support-escaped</eventId>
                    <pawnThingId>escaped-support</pawnThingId>
                    <pawnGlobalKey>owner:ally/colony:ally-colony/snapshot:support/map:caravan/thing:escaped-support</pawnGlobalKey>
                    <autoReturnOnSettlement>True</autoReturnOnSettlement>
                  </li>
                </clashOfRimActiveSupportAssignments>
                <clashOfRimActiveRemoteMapSession>
                  <kind>RaidBattle</kind>
                  <relatedEventId>raid-timeout-001</relatedEventId>
                </clashOfRimActiveRemoteMapSession>
              </li>
            </components>
            <world>
              <worldObjects>
                <worldObjects>
                  <li Class="MapParent">
                    <def>Settlement</def>
                    <ID>1</ID>
                    <tile>10</tile>
                  </li>
                  <li Class="AIRsLight.ClashOfRim.RemoteMaps.RemoteSessionMapParent">
                    <def>ClashOfRim_RemoteRaidBattleMapParent</def>
                    <ID>123</ID>
                    <tile>20</tile>
                    <clashOfRimMode>RaidBattle</clashOfRimMode>
                    <relatedEventId>raid-timeout-001</relatedEventId>
                  </li>
                </worldObjects>
              </worldObjects>
              <worldPawns>
                <pawnsAlive />
              </worldPawns>
            </world>
            <initData>
              <startingAndOptionalPawns>
                <li>Thing_raid-defender</li>
                <li>Thing_raid-attacker</li>
              </startingAndOptionalPawns>
            </initData>
            <taleManager>
              <tales>
                <li Class="RimWorld.Tale_SinglePawn">
                  <pawnData>
                    <pawn>Thing_raid-defender</pawn>
                    <kind>Colonist</kind>
                  </pawnData>
                </li>
              </tales>
            </taleManager>
            <playLog>
              <entries>
                <li Class="Verse.PlayLogEntry_Interaction">
                  <initiator>Thing_home-colonist</initiator>
                  <recipient>Thing_raid-defender</recipient>
                </li>
              </entries>
            </playLog>
            <battleLog>
              <entries>
                <li Class="Verse.BattleLogEntry_ExplosionImpact">
                  <initiatorPawn>Thing_raid-defender</initiatorPawn>
                  <recipientPawn>Thing_home-colonist</recipientPawn>
                </li>
              </entries>
            </battleLog>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <mapInfo><parent>WorldObject_1</parent></mapInfo>
                <things>
                  <thing Class="Pawn">
                    <id>home-colonist</id>
                    <def>Human</def>
                    <kindDef>Colonist</kindDef>
                    <relations>
                      <directRelations>
                        <li>
                          <def>Lover</def>
                          <otherPawn>Thing_raid-defender</otherPawn>
                        </li>
                        <li>
                          <def>Friend</def>
                          <otherPawn>Thing_raid-attacker</otherPawn>
                        </li>
                      </directRelations>
                      <pregnancyApproaches>
                        <keys>
                          <li>Thing_raid-defender</li>
                          <li>Thing_raid-attacker</li>
                        </keys>
                        <values>
                          <li>Normal</li>
                          <li>AvoidPregnancy</li>
                        </values>
                      </pregnancyApproaches>
                    </relations>
                    <needs>
                      <mood>
                        <thoughts>
                          <memories>
                            <li Class="RimWorld.Thought_MemorySocial">
                              <otherPawn>Thing_raid-defender</otherPawn>
                            </li>
                          </memories>
                        </thoughts>
                      </mood>
                    </needs>
                  </thing>
                </things>
              </li>
              <li>
                <uniqueID>9</uniqueID>
                <mapInfo><parent>WorldObject_123</parent></mapInfo>
                <things>
                  <thing Class="Pawn">
                    <id>raid-attacker</id>
                    <def>Human</def>
                    <kindDef>Colonist</kindDef>
                    <genes>
                      <xenogenes>
                        <li Class="Verse.Gene">
                          <def>Robust</def>
                          <loadID>0</loadID>
                        </li>
                      </xenogenes>
                      <endogenes>
                        <li Class="Verse.Gene">
                          <def>StrongMeleeDamage</def>
                          <loadID>0</loadID>
                        </li>
                      </endogenes>
                    </genes>
                  </thing>
                  <thing Class="Pawn">
                    <id>raid-support</id>
                    <def>Human</def>
                    <kindDef>Colonist</kindDef>
                    <genes>
                      <xenogenes>
                        <li Class="Verse.Gene">
                          <def>Robust</def>
                          <loadID>0</loadID>
                        </li>
                      </xenogenes>
                    </genes>
                  </thing>
                  <thing Class="Pawn"><id>raid-defender</id><def>Human</def><kindDef>Colonist</kindDef></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    bool cleaned = RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(
        original,
        "attacker-after-timeout-cleanup",
        DateTimeOffset.UnixEpoch.AddMinutes(1),
        out SaveSnapshotPackage cleanedPackage,
        out RaidAttackerSnapshotCleanupResult cleanupResult);

    Require(cleaned, "应清理远程袭击战场状态");
    Equal(1, cleanupResult.RemovedMapCount, "只应移除远程袭击地图");
    Equal(1, cleanupResult.LostAttackPawns.Count, "只应把仍在战场的进攻方 pawn 记录为失踪");
    Equal("raid-attacker", cleanupResult.LostAttackPawns.Single().LocalId, "撤离的初始进攻 pawn 不应被误算失踪");
    Equal(1, cleanupResult.LostSupportPawns.Count, "只应把仍在战场的支援 pawn 记录为损失");
    Equal("support-in-raid", cleanupResult.LostSupportPawns.Single().EventId, "已离开地图的支援事件不应被误算损失");
    Equal("attacker-after-timeout-cleanup", cleanedPackage.Envelope.Identity.SnapshotId, "清理后快照 id");
    Equal("attacker-before-timeout", cleanedPackage.Envelope.PreviousSnapshotId, "清理后快照应连接原攻击方快照");

    XDocument cleanedDocument;
    using (var stream = new MemoryStream(cleanedPackage.Payload))
    {
        cleanedDocument = XDocument.Load(stream);
    }

    IReadOnlyList<XElement> maps = cleanedDocument.Root!
        .Element("game")!
        .Element("maps")!
        .Elements("li")
        .ToList();
    Equal(1, maps.Count, "主地图应保留且远程地图应移除");
    Equal("0", maps.Single().Element("uniqueID")?.Value, "保留的地图应是主地图");
    Equal("0", cleanedDocument.Root!.Element("game")!.Element("currentMapIndex")?.Value, "清理远程战场后当前地图索引应回到主地图");
    Require(maps.Single().Descendants("id").Any(id => id.Value == "home-colonist"), "主地图殖民者不应被清理");
    Require(maps.Single().Descendants("id").All(id => id.Value != "raid-attacker"), "损失的进攻 pawn 不应留在地图上");
    Require(maps.Single().Descendants("id").All(id => id.Value != "raid-support"), "损失的支援 pawn 不应留在地图上");
    XElement pawnsAlive = cleanedDocument.Root!
        .Element("game")!
        .Element("world")!
        .Element("worldPawns")!
        .Element("pawnsAlive")!;
    XElement attackerWorldPawn = pawnsAlive.Elements("li").Single(pawn => pawn.Element("id")?.Value == "raid-attacker");
    XElement supportWorldPawn = pawnsAlive.Elements("li").Single(pawn => pawn.Element("id")?.Value == "raid-support");
    Require(attackerWorldPawn.Element("map") is null, "迁移到 worldPawns 的进攻 pawn 不应保留地图编号");
    Require(attackerWorldPawn.Element("pos") is null, "迁移到 worldPawns 的进攻 pawn 不应保留地图坐标");
    Require(attackerWorldPawn.Element("jobs")?.Element("curJob")?.Attribute("IsNull")?.Value == "True", "迁移到 worldPawns 的进攻 pawn 应清空当前工作");
    Require(supportWorldPawn.Element("map") is null, "迁移到 worldPawns 的支援 pawn 不应保留地图编号");
    IReadOnlyList<string> movedGeneLoadIds = pawnsAlive
        .Descendants("loadID")
        .Select(loadId => loadId.Value.Trim())
        .Where(value => int.TryParse(value, out _))
        .ToList();
    Equal(movedGeneLoadIds.Count, movedGeneLoadIds.Distinct(StringComparer.Ordinal).Count(), "迁移到 worldPawns 的基因 loadID 不应重复");
    Require(cleanedDocument.Descendants("startingAndOptionalPawns").Elements("li").Any(element => element.Value.Trim() == "Thing_raid-attacker"), "已迁入 worldPawns 的进攻 pawn 引用应保留");
    Require(cleanedDocument.Descendants("startingAndOptionalPawns").Elements("li").All(element => element.Value.Trim() != "Thing_raid-defender"), "被删除远程 pawn 不应留在开局 pawn 列表");
    Require(!cleanedDocument.Descendants("directRelations").Elements("li").Any(element => element.Descendants("otherPawn").Any(pawn => pawn.Value.Trim() == "Thing_raid-defender")), "被删除远程 pawn 不应留在直接关系中");
    Require(cleanedDocument.Descendants("directRelations").Elements("li").Any(element => element.Descendants("otherPawn").Any(pawn => pawn.Value.Trim() == "Thing_raid-attacker")), "已迁入 worldPawns 的进攻 pawn 关系应保留");
    XElement pregnancyApproaches = cleanedDocument.Descendants("pregnancyApproaches").Single();
    Equal(
        pregnancyApproaches.Element("keys")!.Elements("li").Count(),
        pregnancyApproaches.Element("values")!.Elements("li").Count(),
        "清理怀孕策略引用时应保持字典 keys/values 数量一致");
    Require(!cleanedDocument.Descendants().Where(element => !element.HasElements).Any(element => element.Value.Trim() == "Thing_raid-defender"), "清理后不应残留被删除远程 pawn 的直接引用");
    Require(!cleanedDocument.Descendants("clashOfRimActiveRaidBattleSession").Any(), "活动袭击会话应清除");
    Require(!cleanedDocument.Descendants("clashOfRimActiveRemoteMapSession").Any(), "活动远程地图会话应清除");
    Equal("0", cleanedPackage.Index.Maps.Single().UniqueId, "重建索引应只包含主地图");
    Require(cleanedPackage.Index.Pawns.Any(pawn =>
            pawn.LocalId == "raid-attacker"
            && pawn.MapUniqueId is null
            && pawn.Source == "worldPawns/pawnsAlive"),
        "重建索引应把损失进攻 pawn 视为 world pawn");
    Require(cleanedPackage.Index.Pawns.Any(pawn =>
            pawn.LocalId == "raid-support"
            && pawn.MapUniqueId is null
            && pawn.Source == "worldPawns/pawnsAlive"),
        "重建索引应把损失支援 pawn 视为 world pawn");
}

static void VerifyRaidAttackerSnapshotCleanupIgnoresNullRemoteMapSession()
{
    var identity = new SnapshotIdentity("attacker", "colony-a", "attacker-no-raid");
    SaveSnapshotPackage original = XmlSettlementPackage(
        identity,
        """
        <?xml version="1.0" encoding="utf-8"?>
        <savegame>
          <meta><gameVersion>1.6-test</gameVersion></meta>
          <game>
            <components>
              <li Class="AIRsLight.ClashOfRim.ClashOfRimGameComponent">
                <clashOfRimLineageSnapshotId>attacker-no-raid</clashOfRimLineageSnapshotId>
                <clashOfRimLineageToken>base-token</clashOfRimLineageToken>
                <clashOfRimActiveRemoteMapSession IsNull="True" />
              </li>
            </components>
            <world>
              <worldObjects>
                <worldObjects>
                  <li Class="MapParent">
                    <def>Settlement</def>
                    <ID>1</ID>
                    <tile>10</tile>
                  </li>
                </worldObjects>
              </worldObjects>
            </world>
            <maps>
              <li>
                <uniqueID>0</uniqueID>
                <mapInfo><parent>WorldObject_1</parent></mapInfo>
                <things>
                  <thing Class="Pawn"><id>home-colonist</id><def>Human</def><kindDef>Colonist</kindDef></thing>
                </things>
              </li>
            </maps>
          </game>
        </savegame>
        """);

    bool cleaned = RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(
        original,
        "attacker-no-raid-cleanup",
        DateTimeOffset.UnixEpoch.AddMinutes(1),
        out SaveSnapshotPackage cleanedPackage,
        out int removedMapCount);

    Require(!cleaned, "空的远程地图会话占位不应生成清理快照");
    Equal(0, removedMapCount, "空远程地图会话不应移除地图");
    Equal(original.Envelope.Identity.SnapshotId, cleanedPackage.Envelope.Identity.SnapshotId, "未清理时应返回原快照");
}

static void VerifyStackReductionDiff()
{
    ThingSummary original = Thing("steel-stack", stackCount: "75");
    ThingSummary returned = original with { StackCount = "70" };

    RaidSettlementDiffResult diff = RaidSettlementDiffer.CompareByDisappearance(
        new[] { original },
        new[] { returned },
        new RaidSettlementPolicy(0.5, "event-stack-reduction"));

    Equal(0, diff.StolenThingCount, "未整条消失的对象数量");
    Equal(1, diff.ReducedStackThingCount, "堆叠减少对象数量");
    Equal(5, diff.TotalStolenStackCount, "被抢堆叠数量");
    Equal(1, diff.Losses.Count, "损失明细数量");

    RaidSettlementLoss loss = diff.Losses[0];
    Equal(75, loss.OriginalStackCount, "原堆叠数量");
    Equal(70, loss.ReturnedStackCount, "返回堆叠数量");
    Equal(5, loss.StolenStackCount, "被抢堆叠数量明细");
    Equal(37, loss.BaseLossCapCount, "75 的 50% 基础损失上限");
    Equal(0.5, loss.FractionalCapChance, "75 的 50% 上限小数概率");
    Require(loss.MaxLossCount is 37 or 38, "75 的 50% 损失上限只能为 37 或 38");
    Equal(5, loss.LossCount, "75 抢 5 后最终只损失 5");
}

static void VerifyFractionalStackLoss()
{
    ThingSummary single = Thing("single", stackCount: "1");
    RaidSettlementLoss singleLoss = RaidSettlementDiffer.CalculateLoss(single, new RaidSettlementPolicy(0.5, "single-event"));
    RaidSettlementLoss repeatedSingleLoss = RaidSettlementDiffer.CalculateLoss(single, new RaidSettlementPolicy(0.5, "single-event"));

    Equal(1, singleLoss.OriginalStackCount, "单件堆叠数量");
    Equal(1, singleLoss.StolenStackCount, "单件被抢堆叠数量");
    Equal(0, singleLoss.BaseLossCapCount, "单件基础损失上限");
    Equal(0.5, singleLoss.FractionalCapChance, "单件上限小数概率");
    Equal(singleLoss.LossCount, repeatedSingleLoss.LossCount, "同事件同对象应稳定");
    Require(singleLoss.LossCount is 0 or 1, "单件 50% 损失只能为 0 或 1");

    ThingSummary odd = Thing("odd", stackCount: "5");
    RaidSettlementLoss roundedDown = FindLoss(odd, rollMustBeBelowChance: false);
    RaidSettlementLoss roundedUp = FindLoss(odd, rollMustBeBelowChance: true);

    Equal(5, roundedDown.OriginalStackCount, "奇数堆叠数量");
    Equal(2, roundedDown.BaseLossCapCount, "奇数堆叠基础损失上限");
    Equal(0.5, roundedDown.FractionalCapChance, "奇数堆叠上限小数概率");
    Equal(2, roundedDown.LossCount, "奇数堆叠向下结果");
    Equal(3, roundedUp.LossCount, "奇数堆叠向上结果");
}

static RaidSettlementLoss FindLoss(ThingSummary thing, bool rollMustBeBelowChance)
{
    for (int i = 0; i < 1000; i++)
    {
        RaidSettlementLoss loss = RaidSettlementDiffer.CalculateLoss(thing, new RaidSettlementPolicy(0.5, $"event-{i}"));
        if ((loss.FractionalRoll < loss.FractionalCapChance) == rollMustBeBelowChance)
        {
            return loss;
        }
    }

    throw new InvalidOperationException("没有找到符合条件的确定性取整样本。");
}

static SaveIndexReadContext ThirdPartyContext(IReadOnlyList<string> modIds)
{
    return new SaveIndexReadContext(
        new SnapshotIdentity("userA", "colonyA", "third-party-extension-test"),
        "Map_0",
        new SaveMetaSummary("1.6-test", modIds, Array.Empty<string>(), Array.Empty<string>()));
}

static SaveSnapshotIndex SnapshotWithThings(params ThingSummary[] things)
{
    return new SaveSnapshotIndex(
        "raid-trap-fixture.rws",
        new SaveMetaSummary("1.6-test", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        Array.Empty<FactionSummary>(),
        Array.Empty<SaveIndexExtensionData>(),
        Array.Empty<WorldObjectSummary>(),
        new[]
        {
            new MapSummary(
                "Map_0",
                "Generated_0",
                "WorldObject_1",
                "(250, 1, 250)",
                HasCompressedThingMap: true,
                HasTerrainGrid: true,
                HasRoofGrid: true,
                HasFogGrid: true,
                things.Length,
                things.Count(thing => thing.IsPawn))
        },
        things,
        Array.Empty<PawnSummary>());
}

static SaveSnapshotPackage SettlementPackage(SnapshotIdentity identity, string sourceFileName, params ThingSummary[] things)
{
    string mapUniqueId = things.Select(thing => thing.MapUniqueId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? "0";
    var index = new SaveSnapshotIndex(
        sourceFileName,
        new SaveMetaSummary("1.6-test", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        Array.Empty<FactionSummary>(),
        Array.Empty<SaveIndexExtensionData>(),
        Array.Empty<WorldObjectSummary>(),
        new[]
        {
            new MapSummary(
                mapUniqueId,
                $"Generated_{mapUniqueId}",
                "WorldObject_1",
                "(250, 1, 250)",
                HasCompressedThingMap: true,
                HasTerrainGrid: true,
                HasRoofGrid: true,
                HasFogGrid: true,
                things.Length,
                things.Count(thing => thing.IsPawn))
        },
        things,
        Array.Empty<PawnSummary>());

    var envelope = new SaveSnapshotEnvelope(
        SaveSnapshotPackageBuilder.CurrentPackageVersion,
        identity,
        DateTimeOffset.UnixEpoch,
        sourceFileName,
        "1.6-test",
        SnapshotPayloadEncoding.RawRws,
        OriginalSaveBytes: 0,
        PayloadBytes: 0,
        OriginalSha256: new string('a', 64),
        PayloadSha256: new string('a', 64));

    return new SaveSnapshotPackage(envelope, Array.Empty<byte>(), index);
}

static SaveSnapshotPackage XmlSettlementPackage(SnapshotIdentity identity, string xml)
{
    string path = Path.Combine(Path.GetTempPath(), "clashofrim-save-test-" + Guid.NewGuid().ToString("N") + ".rws");
    try
    {
        File.WriteAllText(path, xml);
        return SaveSnapshotPackageBuilder.FromFile(
            path,
            identity,
            DateTimeOffset.UnixEpoch,
            SnapshotPayloadEncoding.RawRws);
    }
    finally
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

static ThingSummary ThingWithDef(string localId, string defName, string className)
{
    string globalKey = $"owner:userA/colony:colonyA/snapshot:raid001/map:Map_0/thing:{localId}";
    return new ThingSummary(
        localId,
        globalKey,
        "Map_0",
        className,
        defName,
        "(1, 0, 1)",
        "Faction_0",
        "1",
        "100",
        null,
        null,
        IsPawn: false);
}

static ThingSummary Thing(string localId, string stackCount)
{
    string globalKey = $"owner:userA/colony:colonyA/snapshot:raid001/map:0/thing:{localId}";
    return new ThingSummary(
        localId,
        globalKey,
        "0",
        "ThingWithComps",
        "Steel",
        "(1, 0, 1)",
        "Faction_0",
        stackCount,
        "100",
        null,
        null,
        IsPawn: false);
}

static byte[] FloatRecords(params float[] values)
{
    byte[] bytes = new byte[values.Length * sizeof(float)];
    for (int index = 0; index < values.Length; index++)
    {
        BitConverter.GetBytes(values[index]).CopyTo(bytes, index * sizeof(float));
    }

    return bytes;
}

static byte[] DeflateBytes(byte[] bytes)
{
    using var target = new MemoryStream();
    using (var deflate = new DeflateStream(target, CompressionMode.Compress, leaveOpen: true))
    {
        deflate.Write(bytes, 0, bytes.Length);
    }

    return target.ToArray();
}

static string? ThingDef(XElement thing)
{
    return thing.Element("def")?.Value;
}

static string? ThingPos(XElement thing)
{
    return thing.Element("pos")?.Value;
}

static int ThingIdNumber(XElement thing)
{
    string value = thing.Element("id")?.Value ?? string.Empty;
    int start = value.Length;
    while (start > 0 && char.IsDigit(value[start - 1]))
    {
        start--;
    }

    return int.Parse(value[start..]);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}：期望 {expected}，实际 {actual}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
