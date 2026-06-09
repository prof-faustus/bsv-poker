using BsvPoker.Core; using BsvPoker.Crypto;
string target = "1BH5Uf3tbfNSBjCmVJYWB5nTCRXVVBQXuR";
string Addr(byte[] pub){ var p=new byte[21]; p[0]=0x00; Hashes.Hash160(pub).CopyTo(p,1); return Base58.CheckEncode(p); }
void Check(string name, string backup){
  byte[] seed; try{ seed=WalletKeys.BackupToSeed(backup); }catch(Exception e){ Console.WriteLine($"{name}: seed not decodable ({e.Message})"); return; }
  // identity address (Base ID), and chain-0 receive addresses 0..200, change chain-1 0..50
  var idPub = Secp256k1.PublicKeyCompressed(Type42.UniqueKey(seed,"bsvpoker/identity"));
  if(Addr(idPub)==target){ Console.WriteLine($"*** {name}: IDENTITY address == target ***"); return; }
  for(uint c=0;c<=1;c++) for(uint i=0;i<300;i++){ if(Addr(WalletKeys.Account(seed,c,i).Pub)==target){ Console.WriteLine($"*** {name}: chain {c} index {i} == target ***"); return; } }
  Console.WriteLine($"{name}: target NOT derived (checked identity + chain0/1 x300). First recv addr = {Addr(WalletKeys.Account(seed,0,0).Pub)}");
}
Check("p2", "6Eq8ddQHPjqrHkVKrG1pqUFFoVDpMK2Fz4ShHu9mgHJMh9XW1jc");
Check("root", "6EQ2nFUWsV3trbJZHXGBgYWjBnsVH7KgjNT5bLRRaxsKirrSC8f");
