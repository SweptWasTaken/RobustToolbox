﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Lidgren.Network;
using SS13.IoC;
using SS13_Shared;
using SS13_Shared.ServerEnums;
using ServerInterfaces.Map;
using ServerInterfaces.Network;
using ServerInterfaces.Tiles;
using ServerServices.Atmos;
using ServerServices.Log;
using ServerServices.Tiles;

namespace ServerServices.Map
{
    public class MapManager : IMapManager
    {
        #region Variables

        private DateTime lastAtmosDisplayPush;
        private int mapHeight;
        private int mapWidth;
        public int tileSpacing = 64;
        private Dictionary<byte, string> tileStringTable = new Dictionary<byte, string>();
        private RectangleTree<Tile> tileArray;
        private RectangleF worldArea;

        #endregion

        #region Startup

        public bool InitMap(string mapName)
        {
            BuildTileTable();
            if (!LoadMap(mapName))
                NewMap();

            return true;
        }

        #endregion

        #region IMapManager Members

        /// <summary>
        /// This function takes the gas cell from one tile and moves it to another, reconnecting all of the references in adjacent tiles.
        /// Use this when a new tile is generated at a map location.
        /// </summary>
        /// <param name="fromTile">Tile to move gas information/cell from</param>
        /// <param name="toTile">Tile to move gas information/cell to</param>
        public void MoveGasCell(ITile fromTile, ITile toTile)
        {
            if (fromTile == null)
                return;
            GasCell g = (fromTile as Tile).gasCell;
            (toTile as Tile).gasCell = g;
            g.AttachToTile((toTile as Tile));
        }

        public void Shutdown()
        {
            //ServiceManager.Singleton.RemoveService(this);
            tileArray = null;
        }

        public int GetMapWidth()
        {
            return mapWidth;
        }

        public int GetMapHeight()
        {
            return mapHeight;
        }

        #endregion

        #region Tile helper function

        public int GetTileSpacing()
        {
            return tileSpacing;
        }

        public ITile[] GetITilesIn(RectangleF area)
        {
            return tileArray.GetItems(new Rectangle((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height));
        }

        public ITile GetITileAt(Vector2 pos)
        {
            return (ITile)tileArray.GetItems(new Point((int)pos.X, (int)pos.Y)).FirstOrDefault();
        }

        public Tile GetTileAt(Vector2 pos)
        {
            return tileArray.GetItems(new Point((int)pos.X, (int)pos.Y)).FirstOrDefault();
        }

        public Point GetTileArrayPositionFromWorldPosition(float x, float z)
        {
            if (x < 0 || z < 0)
                return new Point(-1, -1);
            if (x >= mapWidth*tileSpacing || z >= mapWidth*tileSpacing)
                return new Point(-1, -1);

            // We use floor here, because even if we're at pos 10.999999, we're still on tile 10 in the array.
            var xPos = (int) Math.Floor(x/tileSpacing);
            var zPos = (int) Math.Floor(z/tileSpacing);

            return new Point(xPos, zPos);
        }

        public bool IsWorldPositionInBounds(Vector2 pos)
        {
            Point tpos = GetTileArrayPositionFromWorldPosition(pos);
            if (tpos.X == -1 && tpos.Y == -1)
                return false;
            return true;
        }

        public Point GetTileArrayPositionFromWorldPosition(Vector2 pos)
        {
            return GetTileArrayPositionFromWorldPosition(pos.X, pos.Y);
        }

        public Type GetTileTypeFromWorldPosition(float x, float y)
        {
            Point arrayPosition = GetTileArrayPositionFromWorldPosition(x, y);
            return GetTileTypeFromWorldPosition(new Vector2(x, y));
        }

        public bool IsSaneArrayPosition(int x, int y)
        {
            if (x < 0 || y < 0)
                return false;
            if (x > mapWidth - 1 || y > mapWidth - 1)
                return false;
            return true;
        }

        private Type GetTileTypeFromWorldPosition(Vector2 pos)
        {
            Point arrayPosition = GetTileArrayPositionFromWorldPosition(pos.X, pos.Y);
            if (arrayPosition.Y < 0 || arrayPosition.Y < 0)
            {
                return null;
            }
            else
            {
                return GetObjectTypeFromArrayPosition(arrayPosition.X, arrayPosition.Y);
            }
        }

        private Type GetObjectTypeFromArrayPosition(int x, int z)
        {
            if (x < 0 || z < 0 || x >= mapWidth || z >= mapHeight)
            {
                return null;
            }
            else
            {
                return GetTileFromIndex(x, z).GetType();
            }
        }

        #endregion

        #region Map altering

        public bool ChangeTile(Vector2 pos, string newType)
        {
            var tile = GenerateNewTile(pos, newType) as Tile;
            //Transfer the gas cell from the old tile to the new tile.
            Tile t = (Tile)GetITileAt(pos);
            MoveGasCell(t, tile);

            tileArray.Remove(t);
            tileArray.Add(tile);
            UpdateTile(pos);
            return true;
        }

        public ITile GenerateNewTile(Vector2 pos, string typeName)
        {
            Type tileType = Type.GetType("ServerServices.Tiles." + typeName, false);

            if (tileType == null) throw new ArgumentException("Invalid Tile Type specified : '" + typeName + "' .");

            Tile t = (Tile)GetTileAt(pos);

            if (t != null) //If theres a tile, activate it's changed event.
                t.RaiseChangedEvent(tileType);

            return (ITile) Activator.CreateInstance(tileType, pos, this);
        }


        #endregion

        #region networking

        public NetOutgoingMessage CreateMapMessage(MapMessage messageType)
        {
            NetOutgoingMessage message = IoCManager.Resolve<ISS13NetServer>().CreateMessage();
            message.Write((byte) NetMessage.MapMessage);
            message.Write((byte) messageType);
            return message;
        }

        public void SendMap(NetConnection connection)
        {
            SendTileIndex(connection); //Send index of byte -> str to save space.

            LogManager.Log(connection.RemoteEndPoint.Address + ": Sending map");
            NetOutgoingMessage mapMessage = CreateMapMessage(MapMessage.SendTileMap);

            int mapWidth = GetMapWidth();
            int mapHeight = GetMapHeight();

            mapMessage.Write(mapWidth);
            mapMessage.Write(mapHeight);

            foreach (Tile t in tileArray.GetItems(new Rectangle(0, 0, mapWidth * tileSpacing, mapHeight * tileSpacing)))
            {
                mapMessage.Write(t.WorldPosition.X);
                mapMessage.Write(t.WorldPosition.Y);
                mapMessage.Write(GetTileIndex((t.GetType().Name)));
                mapMessage.Write((byte)t.TileState);
            }

            IoCManager.Resolve<ISS13NetServer>().SendMessage(mapMessage, connection, NetDeliveryMethod.ReliableOrdered);
            LogManager.Log(connection.RemoteEndPoint.Address + ": Sending map finished with message size: " +
                           mapMessage.LengthBytes + " bytes");
        }

        /// <summary>
        /// Send message to all clients.
        /// </summary>
        /// <param name="message"></param>
        public void SendMessage(NetOutgoingMessage message)
        {
            IoCManager.Resolve<ISS13NetServer>().SendToAll(message);
        }

        public void SendTileIndex(NetConnection connection)
        {
            NetOutgoingMessage mapMessage = CreateMapMessage(MapMessage.SendTileIndex);

            mapMessage.Write((byte) tileStringTable.Count);

            foreach (var curr in tileStringTable)
            {
                mapMessage.Write(curr.Key);
                mapMessage.Write(curr.Value);
            }

            IoCManager.Resolve<ISS13NetServer>().SendMessage(mapMessage, connection, NetDeliveryMethod.ReliableOrdered);
        }

        #endregion

        public void BuildTileTable()
        {
            Type type = typeof (Tile);
            List<Assembly> asses = AppDomain.CurrentDomain.GetAssemblies().ToList();
            List<Type> types =
                asses.SelectMany(t => t.GetTypes()).Where(p => type.IsAssignableFrom(p) && !p.IsAbstract).ToList();

            if (types.Count > 255)
                throw new ArgumentOutOfRangeException("types.Count", "Can not load more than 255 types of tiles.");

            tileStringTable = types.ToDictionary(x => (byte) types.FindIndex(y => y == x), x => x.Name);
        }

        public byte GetTileIndex(string typeName)
        {
            if (tileStringTable.Values.Any(x => x.ToLowerInvariant() == typeName.ToLowerInvariant()))
                return tileStringTable.First(x => x.Value.ToLowerInvariant() == typeName.ToLowerInvariant()).Key;
            else throw new ArgumentNullException("tileStringTable", "Can not find '" + typeName + "' type.");
        }

        public string GetTileString(byte index)
        {
            string typeStr = (from a in tileStringTable
                              where a.Key == index
                              select a.Value).First();

            return typeStr;
        }

        #region Networking

        public void HandleNetworkMessage(NetIncomingMessage message)
        {
            var messageType = (MapMessage) message.ReadByte();
            switch (messageType)
            {
                case MapMessage.TurfClick:
                    //HandleTurfClick(message);
                    break;
                case MapMessage.TurfUpdate:
                    HandleTurfUpdate(message);
                    break;
                default:
                    break;
            }
        }

        /*
        private void HandleTurfClick(NetIncomingMessage message)
        {
            // Who clicked and on what tile.
            Atom.Atom clicker = SS13Server.Singleton.playerManager.GetSessionByConnection(message.SenderConnection).attachedAtom;
            short x = message.ReadInt16();
            short y = message.ReadInt16();

            if (Vector2.Distance(clicker.position, new Vector2(x * tileSpacing + (tileSpacing / 2), y * tileSpacing + (tileSpacing / 2))) > 96)
            {
                return; // They were too far away to click us!
            }
            bool Update = false;
            if (IsSaneArrayPosition(x, y))
            {
                Update = tileArray[x, y].ClickedBy(clicker);
                if (Update)
                {
                    if (tileArray[x, y].tileState == TileState.Dead)
                    {
                        Tiles.Atmos.GasCell g = tileArray[x, y].gasCell;
                        Tiles.Tile t = GenerateNewTile(x, y, tileArray[x, y].tileType);
                        tileArray[x, y] = t;
                        tileArray[x, y].gasCell = g;
                    }
                    NetworkUpdateTile(x, y);
                }
            }
        }*/ // TODO HOOK ME BACK UP WITH ENTITY SYSTEM

        public void DestroyTile(Vector2 pos)
        {
            Tile t = GetTileAt(pos);
            var newTile = GenerateNewTile(pos, "Floor") as Tile; //Ugly
            tileArray.Remove(t);
            tileArray.Add(newTile);
            MoveGasCell(t, newTile);
            NetworkUpdateTile(pos);
            UpdateTile(pos);
        }

        public void UpdateTile(Vector2 pos)
        {
            Tile t = (Tile)GetTileAt(pos);
            if (t == null)
                return;
            t.gasCell.SetNeighbours(this);
            foreach(Tile u in GetITilesIn(new RectangleF(pos.X - tileSpacing, pos.Y - tileSpacing, tileSpacing * 2, tileSpacing * 2)))
            {
                u.gasCell.SetNeighbours(this);
            }
        }


        public void NetworkUpdateTile(Vector2 pos)
        {
            Tile t = (Tile)GetTileAt(pos);
            if (t == null)
                return;
            NetOutgoingMessage message = IoCManager.Resolve<ISS13NetServer>().CreateMessage();
            message.Write((byte) NetMessage.MapMessage);
            message.Write((byte) MapMessage.TurfUpdate);
            message.Write(pos.X);
            message.Write(pos.Y);
            message.Write(GetTileIndex(t.GetType().Name));
            message.Write((byte) t.TileState);
            IoCManager.Resolve<ISS13NetServer>().SendToAll(message);
        }

        private void HandleTurfUpdate(NetIncomingMessage message)
        {
            float x = message.ReadFloat();
            float y = message.ReadFloat();
            Vector2 pos = new Vector2(x, y);
            string typeStr = GetTileString(message.ReadByte());

            Tile tile = GetTileAt(pos);

            GasCell g = tile.gasCell;
            var t = GenerateNewTile(pos, typeStr) as Tile;
            MoveGasCell(tile, t);
            tileArray.Remove(tile);
            tileArray.Add(t);
            NetworkUpdateTile(pos);
            UpdateTile(pos);

        }

        #endregion

        #region Map loading/sending

        public void SaveMap()
        {
            string fileName = "SavedMap";

            var fs = new FileStream(fileName, FileMode.Create);
            var sw = new StreamWriter(fs);
            LogManager.Log("Saving map: W: " + mapWidth + " H: " + mapHeight);

            sw.WriteLine(mapWidth);
            sw.WriteLine(mapHeight);

            foreach (Tile t in tileArray.GetItems(new Rectangle(0, 0, mapWidth * tileSpacing, mapHeight * tileSpacing)))
            {
                sw.WriteLine(t.WorldPosition.X);
                sw.WriteLine(t.WorldPosition.Y);
                sw.WriteLine(GetTileIndex(t.GetType().Name));
            }

            LogManager.Log("Done saving map.");

            sw.Close();
            fs.Close();
        }

        private Rectangle TilePos(Tile T)
        {
            return new Rectangle((int)(T.WorldPosition.X), (int)(T.WorldPosition.Y), (int)(tileSpacing), (int)(tileSpacing));
        }


        public ITile GetTileFromIndex(int x, int y)
        {
            return (Tile)GetTileFromWorldPosition(x * tileSpacing, y * tileSpacing);
        }

        public ITile GetTileFromWorldPosition(Vector2 v)
        {
            return GetTileFromWorldPosition(v.X, v.Y);
        }

        public ITile GetTileFromWorldPosition(float x, float y)
        {
            Point p = new Point((int)x, (int)y);
            return (Tile)tileArray.GetItems(p).FirstOrDefault();
        }

        public RectangleF GetWorldArea()
        {
            return worldArea;
        }

        private bool LoadMap(string filename)
        {
            if (!File.Exists(filename))
                return false;

            var fs = new FileStream(filename, FileMode.Open);
            var sr = new StreamReader(fs);

            mapWidth = int.Parse(sr.ReadLine());
            mapHeight = int.Parse(sr.ReadLine());

            worldArea = new RectangleF(0, 0, mapWidth * tileSpacing, mapHeight * tileSpacing);

            tileArray = new RectangleTree<Tile>(TilePos, new Rectangle(-(mapWidth / 2) * tileSpacing, -(mapHeight / 2) * tileSpacing,
                                                                 mapWidth * tileSpacing, mapHeight * tileSpacing));

            while (!sr.EndOfStream)
            {
                float x = float.Parse(sr.ReadLine());
                float y = float.Parse(sr.ReadLine());
                byte i = byte.Parse(sr.ReadLine());

                tileArray.Add((Tile)GenerateNewTile(new Vector2(x, y), GetTileString(i)));
            }

            sr.Close();
            fs.Close();

            return true;
        }

        private void NewMap()
        {
            LogManager.Log("Cannot find map. Generating blank map.", LogLevel.Warning);
            mapWidth = 50;
            mapHeight = 50;
            tileArray = new RectangleTree<Tile>(TilePos, new Rectangle(-(mapWidth / 2) * tileSpacing, -(mapHeight / 2) * tileSpacing,
                                                                             mapWidth * tileSpacing, mapHeight * tileSpacing));

            worldArea = new RectangleF(0, 0, mapWidth * tileSpacing, mapHeight * tileSpacing);

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    tileArray.Add(new Floor(new Vector2(x * tileSpacing, y * tileSpacing), this));
                }
            }
        }

        #endregion
    }
}