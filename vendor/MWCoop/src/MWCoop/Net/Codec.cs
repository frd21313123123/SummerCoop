using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace MWCoop.Net
{
    // Ecriture/lecture binaire des messages (petit-boutiste, textes UTF-8 prefixes par leur longueur).
    public class NetWriter : IDisposable
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct SingleBits { [System.Runtime.InteropServices.FieldOffset(0)] public float Value; [System.Runtime.InteropServices.FieldOffset(0)] public int Bits; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct DoubleBits { [System.Runtime.InteropServices.FieldOffset(0)] public double Value; [System.Runtime.InteropServices.FieldOffset(0)] public long Bits; }
        [ThreadStatic] static System.Collections.Generic.Stack<byte[]> buffers;
        byte[] buffer;
        int position;
        readonly bool pooled;
        public NetWriter(Msg type) : this(type, false) { }
        NetWriter(Msg type, bool rent)
        {
            pooled = rent;
            buffer = rent && buffers != null && buffers.Count > 0 ? buffers.Pop() : new byte[256];
            buffer[position++] = (byte)type;
        }
        public static NetWriter Rent(Msg type) { return new NetWriter(type, true); }
        void Need(int count)
        {
            if (buffer == null) throw new ObjectDisposedException("NetWriter");
            if (count < 0 || position > int.MaxValue - count) throw new IOException("Packet too large");
            if (position + count > buffer.Length) Array.Resize(ref buffer, Math.Max(position + count, buffer.Length * 2));
        }
        public NetWriter U8(int v) { Need(1); buffer[position++] = (byte)v; return this; }
        public NetWriter U16(int v) { Need(2); buffer[position++] = (byte)v; buffer[position++] = (byte)(v >> 8); return this; }
        public NetWriter I32(int v) { Need(4); for (int i = 0; i < 4; i++) buffer[position++] = (byte)(v >> (i * 8)); return this; }
        public NetWriter F32(float v) { return I32(new SingleBits { Value = v }.Bits); }
        public NetWriter F64(double v) { long bits = new DoubleBits { Value = v }.Bits; Need(8); for (int i = 0; i < 8; i++) buffer[position++] = (byte)(bits >> (i * 8)); return this; }
        public NetWriter Bool(bool v) { return U8(v ? 1 : 0); }
        public NetWriter Vec(Vector3 v) { return F32(v.x).F32(v.y).F32(v.z); }
        public NetWriter Quat(Quaternion q) { return F32(q.x).F32(q.y).F32(q.z).F32(q.w); }
        public NetWriter Str(string s)
        {
            s = s ?? "";
            int length = Encoding.UTF8.GetByteCount(s);
            if (length > 16384) throw new IOException("Network string too long");
            Need(length + 2); U16(length);
            position += Encoding.UTF8.GetBytes(s, 0, s.Length, buffer, position);
            return this;
        }
        public NetWriter Bytes(byte[] b, int off, int n) { if (b == null || off < 0 || n < 0 || n > ushort.MaxValue || off > b.Length - n) throw new IOException("Invalid byte range"); U16(n); return Raw(b, off, n); }
        public NetWriter Raw(byte[] b) { if (b == null) throw new ArgumentNullException("b"); return Raw(b, 0, b.Length); }
        public NetWriter Raw(byte[] b, int off, int n) { if (b == null || off < 0 || n < 0 || off > b.Length - n) throw new IOException("Invalid byte range"); Need(n); Buffer.BlockCopy(b, off, buffer, position, n); position += n; return this; }
        public byte[] ToArray() { Need(0); byte[] owned = new byte[position]; Buffer.BlockCopy(buffer, 0, owned, 0, position); return owned; }
        public int Length { get { return position; } }
        public void Dispose()
        {
            if (buffer == null) return;
            if (pooled && buffer.Length <= 65536)
            {
                if (buffers == null) buffers = new System.Collections.Generic.Stack<byte[]>();
                if (buffers.Count < 32) buffers.Push(buffer);
            }
            buffer = null;
        }
    }

    public class NetReader
    {
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        readonly byte[] b;
        int p;
        readonly int end;
        public NetReader(byte[] data, int start, int len) { if (data == null || start < 0 || len < 0 || start > data.Length - len) throw new IOException("Invalid packet range"); b = data; p = start; end = start + len; }
        public bool More { get { return p < end; } }
        // Copie de ce qui reste a lire (sans avancer).
        public byte[] Rest() { var r = new byte[end - p]; Buffer.BlockCopy(b, p, r, 0, r.Length); return r; }
        void Need(int n) { if (n < 0 || n > end - p) throw new EndOfStreamException("message tronque"); }
        public int U8() { Need(1); return b[p++]; }
        public int U16() { Need(2); int v = b[p] | b[p + 1] << 8; p += 2; return v; }
        public int I32() { Need(4); int v = BitConverter.ToInt32(b, p); p += 4; return v; }
        public float F32() { Need(4); float v = BitConverter.ToSingle(b, p); p += 4; if (float.IsNaN(v) || float.IsInfinity(v)) throw new IOException("Invalid network float"); return v; }
        // NaN is the existing wire sentinel for an absent cabin heater. Only
        // explicitly optional fields may use it; positions/velocities stay strict.
        public float OptionalF32() { Need(4); float v = BitConverter.ToSingle(b, p); p += 4; if (float.IsInfinity(v)) throw new IOException("Invalid optional network float"); return v; }
        public double F64() { Need(8); double v = BitConverter.ToDouble(b, p); p += 8; if (double.IsNaN(v) || double.IsInfinity(v)) throw new IOException("Invalid network number"); return v; }
        public bool Bool() { int value = U8(); if (value > 1) throw new IOException("Invalid network boolean"); return value != 0; }
        public Vector3 Vec() { return new Vector3(F32(), F32(), F32()); }
        public Quaternion Quat() { return new Quaternion(F32(), F32(), F32(), F32()); }
        public string Str() { int n = U16(); if (n > 16384) throw new IOException("Network string too long"); Need(n); string s; try { s = StrictUtf8.GetString(b, p, n); } catch (DecoderFallbackException e) { throw new IOException("Invalid UTF8", e); } p += n; return s; }
        public byte[] Bytes() { int n = U16(); Need(n); var r = new byte[n]; Buffer.BlockCopy(b, p, r, 0, n); p += n; return r; }
    }

    // Types de messages. Ajouter a la fin ; changer le sens d'un message = monter Session.NetVersion.
    public enum Msg : byte
    {
        Hello = 1,        // invite -> hote : pseudo, apparence, version du mod
        Roster = 2,       // hote -> tous : liste des joueurs
        PlayerState = 3,  // non fiable : position, regard, posture d'un joueur
        Chat = 4,
        World = 5,        // hote -> invites : heure, jour, meteo
        SaveBegin = 6,    // hote -> invite : debut d'envoi de la sauvegarde
        SaveChunk = 7,
        SaveEnd = 8,
        StartGame = 9,    // (reserve)
        Profile = 10,     // un joueur change de pseudo ou d'apparence
        Interact = 11,    // porte, interrupteur... actionne par un joueur
        Vehicle = 12,     // non fiable : position et vitesses d'une voiture
        Part = 13,        // piece montee ou demontee
        Bolt = 14,        // vis serree ou desserree d'un cran
        Prop = 15,        // non fiable : piece libre deplacee (tenue, lachee, au repos)
        Income = 16,      // argent gagne par un joueur, recu par tous
        Purchase = 17,    // achat paye a une caisse (contenu du panier)
        Paint = 18,       // piece repeinte (bombe) ou couleur tiree au hasard chez l'hote
        Setting = 19,     // reglage d'une piece (carburateur, repartiteur...)
        Traffic = 20,     // non fiable : lot de vehicules de la circulation et de passants (hote)
        Job = 21,         // boulot : changement d'etat d'un automate de progression
        Fluid = 22,       // liquides et usure : lot de (cle, valeur) changees
        Consume = 23,     // objet mange, bu ou jete (ID) : il disparait chez tous
        Voice = 24,       // replique d'un PNJ ou juron d'un joueur (groupe/variante MasterAudio)
        WorldFsm = 28,    // action d'un joueur sur un automate du monde (facture payee, cuisiniere...)
        WorldVars = 29,   // hote -> invites : variables des automates sauvegardes et globales House*
        Seat = 27,        // joueur assis a une place passager (voiture, place) ou sorti
        CarVisual = 26,   // tableau de bord, voyants, vitres, leviers... : lot de (cle, drapeaux, pose)
        CarDoor = 25,     // portiere / capot / coffre : fermee (0), ouverte (1), angle (2), saisie (3), verrouillee (4) ; ordre de l'hote
        Machine = 30,     // non fiable : ce qu'on voit d'une machine a ecran (pompe, poker, machine a sous) chez celui qui s'en sert
        Stock = 31,       // magasin : panier d'un joueur et stock d'un produit qui ont change
        Npc = 32,         // non fiable, hote -> invites : PNJ proches des invites (pose, clip, objets tenus)
        Parked = 33,      // hote -> invites : voitures garees du decor (parent, pose, visible)
        Audit = 34,       // audit de la synchro : hote -> invites « SNAP n », invites -> hote leurs etats changes (cle hachee, etat)
        MachineLock = 35, // verrou d'une machine a jeu (poker, machine a sous), arbitre par l'hote : libre, demande, tenue, refus ; jeu lance sur l'ordinateur
        Frost = 36,       // givre et buee des vitres, grattage du pare-brise (a remplir : lot 2)
        Tow = 37,         // corde de remorquage entre deux vehicules (a remplir : lot 2)
        Call = 38,        // appels sortants (telephone fixe, telephone du taxi) (a remplir : lot 2)
        Wear = 39,        // vetements portes (veste, combinaison, casque) (a remplir : lot 2)
        Cook = 40,        // cuisine : saucisses et viande grillees, cafe, peremption (a remplir : lot 4)
        Fire = 41,        // feux : bois dans les cheminees, poeles, sauna, grill ; incendies (a remplir : lot 4)
        Garage = 42,      // atelier : crics, pont, palan, degats d'accident, chargeur du tracteur (a remplir : lot 4)
        Home = 43,        // maison : fusibles et electricite, kilju (a remplir : lot 4)
        Gesture = 44,     // gestes du joueur : coup, doigt, pouce, pipi, montre, pencher, ivresse, assis (a remplir : lot 4)
        Thrown = 45,      // bouteille vide jetee par un joueur qui a bu (etat de l'automate Drink, pose, vitesse)
        Payout = 46,      // enveloppe de paie d'un boulot prise par un joueur : retiree chez les autres
        Race = 47,        // courses : voitures IA (hote), table des resultats du rallye, empreinte de la liste
        PushDoor = 48,    // porte poussee hors vehicule (garage) : angle de celui qui pousse
        Summon = 49,      // hote -> un invite : viens a moi (position, cap) ; menu F10 ou lanceur (Admin)
        Radio = 50,       // hote -> invites : morceau, position et lecture de chaque canal radio (Radio)
        Music = 55,       // authenticated catalogue and bounded music file pulls
        Cd = 56,          // host-ordered CD controls and playback snapshots
        ModData = 51,     // donnees d'un autre mod (CoopApi) : canal, morceaux ; l'hote relaie
        Event = 53,       // evenement du monde decide par l'hote : objets allumes, valeurs reglees (Events)
        Talk = 54,        // vocal de proximite : paquet de voix compresse par Steam (Voice) ; l'hote relaie
        Knob = 52,        // commande tenue d'un vehicule (starter, frein a main) : valeur de celui qui la tient (Knobs)
    }
}
