// Oracle: builds Minter transactions and checks with the upstream minter-go-node v3.3.0 code, so that
// Minter.Protocol can be compared byte for byte. Reads cases (JSON) on stdin, writes results (JSON) on stdout.
//
// Transaction data fields are named as in the Go structs that transaction.GetData (V3) returns and are filled by
// reflection: Address "Mx...", Pubkey "Mp...", CoinSymbol text, *big.Int decimal string, []byte and [N]byte hex,
// integers as JSON numbers, nested structs as objects, slices as arrays. Test keys only; nothing is broadcast.
package main

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math/big"
	"os"
	"reflect"
	"strings"

	"github.com/MinterTeam/minter-go-node/coreV2/check"
	"github.com/MinterTeam/minter-go-node/coreV2/state/accounts"
	"github.com/MinterTeam/minter-go-node/coreV2/state/commission"
	"github.com/MinterTeam/minter-go-node/coreV2/transaction"
	"github.com/MinterTeam/minter-go-node/coreV2/types"
	"github.com/MinterTeam/minter-go-node/crypto"
	"github.com/MinterTeam/minter-go-node/rlp"
)

// uint64 as a JSON number or a decimal string (JavaScript cannot write numbers above 2^53).
type flexUint uint64

func (f *flexUint) UnmarshalJSON(raw []byte) error {
	n, err := parseUint(raw)
	*f = flexUint(n)
	return err
}

func parseUint(raw []byte) (uint64, error) {
	var n uint64
	if err := json.Unmarshal(raw, &n); err == nil {
		return n, nil
	}
	var s string
	if err := json.Unmarshal(raw, &s); err != nil {
		return 0, err
	}
	_, err := fmt.Sscan(s, &n)
	return n, err
}

type txCase struct {
	Name        string          `json:"name"`
	Key         string          `json:"key"`
	Nonce       flexUint        `json:"nonce"`
	ChainID     byte            `json:"chainId"`
	GasPrice    uint32          `json:"gasPrice"`
	GasCoin     uint32          `json:"gasCoin"`
	Type        byte            `json:"type"`
	Data        json.RawMessage `json:"data"`
	Payload     string          `json:"payload"`
	ServiceData string          `json:"serviceData"`
	Multisig    *struct {
		Address string   `json:"address"`
		Keys    []string `json:"keys"`
	} `json:"multisig"`
}

type checkCase struct {
	Name     string `json:"name"`
	Key      string `json:"key"`
	Password string `json:"password"`
	Nonce    string `json:"nonce"`
	ChainID  byte     `json:"chainId"`
	DueBlock flexUint `json:"dueBlock"`
	Coin     uint32 `json:"coin"`
	Value    string `json:"value"`
	GasCoin  uint32 `json:"gasCoin"`
	Redeemer string `json:"redeemer"`
}

type multisigCase struct {
	Owner string   `json:"owner"`
	Nonce flexUint `json:"nonce"`
}

type input struct {
	Prices        json.RawMessage `json:"prices"`
	Transactions  []txCase        `json:"transactions"`
	Checks        []checkCase     `json:"checks"`
	MultisigAddrs []multisigCase  `json:"multisigAddresses"`
}

type txResult struct {
	Name    string   `json:"name"`
	Hex     string   `json:"hex"`
	Hash    string   `json:"hash"`
	Sender  string   `json:"sender"`
	Signers []string `json:"signers,omitempty"`
	Price   string   `json:"price"`
	Decoded string   `json:"decoded"`
}

type checkResult struct {
	Name      string `json:"name"`
	Check     string `json:"check"`
	Proof     string `json:"proof"`
	Issuer    string `json:"issuer"`
	ProofOK   bool   `json:"proofMatchesLock"`
	WrongPass bool   `json:"wrongPasswordMatchesLock"`
}

type output struct {
	Source        string        `json:"source"`
	Transactions  []txResult    `json:"transactions"`
	Checks        []checkResult `json:"checks"`
	MultisigAddrs []string      `json:"multisigAddresses"`
}

func main() {
	var in input
	if err := json.NewDecoder(os.Stdin).Decode(&in); err != nil {
		fail("read cases: %v", err)
	}
	var prices commission.Price
	if err := fill(reflect.ValueOf(&prices).Elem(), in.Prices); err != nil {
		fail("prices: %v", err)
	}
	out := output{Source: "github.com/MinterTeam/minter-go-node v3.3.0 (235a4a2f)"}
	decoder := transaction.NewExecutor(transaction.GetData)
	for _, c := range in.Transactions {
		r, err := buildTx(c, &prices, decoder)
		if err != nil {
			fail("transaction %s: %v", c.Name, err)
		}
		out.Transactions = append(out.Transactions, r)
	}
	for _, c := range in.Checks {
		r, err := buildCheck(c)
		if err != nil {
			fail("check %s: %v", c.Name, err)
		}
		out.Checks = append(out.Checks, r)
	}
	for _, m := range in.MultisigAddrs {
		out.MultisigAddrs = append(out.MultisigAddrs, accounts.CreateMultisigAddress(types.HexToAddress(m.Owner), uint64(m.Nonce)).String())
	}
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(out); err != nil {
		fail("write: %v", err)
	}
}

func buildTx(c txCase, prices *commission.Price, decoder transaction.ExecutorTx) (txResult, error) {
	data, ok := transaction.GetData(transaction.TxType(c.Type))
	if !ok {
		return txResult{}, fmt.Errorf("unknown type 0x%02x", c.Type)
	}
	if err := fill(reflect.ValueOf(data).Elem(), c.Data); err != nil {
		return txResult{}, err
	}
	raw, err := rlp.EncodeToBytes(data)
	if err != nil {
		return txResult{}, err
	}
	tx := &transaction.Transaction{
		Nonce: uint64(c.Nonce), ChainID: types.ChainID(c.ChainID), GasPrice: c.GasPrice, GasCoin: types.CoinID(c.GasCoin),
		Type: transaction.TxType(c.Type), Data: raw, Payload: mustHex(c.Payload), ServiceData: mustHex(c.ServiceData),
		SignatureType: transaction.SigTypeSingle,
	}
	if c.Multisig == nil {
		if err := tx.Sign(key(c.Key)); err != nil {
			return txResult{}, err
		}
	} else {
		tx.SignatureType = transaction.SigTypeMulti
		tx.SetMultisigAddress(types.HexToAddress(c.Multisig.Address))
		for _, k := range c.Multisig.Keys {
			if err := tx.Sign(key(k)); err != nil {
				return txResult{}, err
			}
		}
	}
	encoded, err := tx.Serialize()
	if err != nil {
		return txResult{}, err
	}
	// The network's own decoder must accept the bytes: V3 data, signature and sender.
	decoded, err := decoder.DecodeFromBytes(encoded)
	if err != nil {
		return txResult{}, fmt.Errorf("go decoder rejects the transaction: %v", err)
	}
	sender, err := decoded.Sender()
	if err != nil {
		return txResult{}, err
	}
	r := txResult{Name: c.Name, Hex: hex.EncodeToString(encoded), Sender: sender.String(), Decoded: decoded.GetDecodedData().String()}
	hash := sha256.Sum256(encoded)
	r.Hash = "Mt" + hex.EncodeToString(hash[:])
	if c.Multisig != nil {
		var multi transaction.SignatureMulti
		if err := rlp.DecodeBytes(decoded.SignatureData, &multi); err != nil {
			return txResult{}, err
		}
		h := decoded.Hash()
		for _, s := range multi.Signatures {
			signer, err := transaction.RecoverPlain(h, s.R, s.S, s.V)
			if err != nil {
				return txResult{}, err
			}
			r.Signers = append(r.Signers, signer.String())
		}
	}
	tx.SetDecodedData(data)
	r.Price = tx.Price(prices).String()
	return r, nil
}

func buildCheck(c checkCase) (checkResult, error) {
	value, ok := new(big.Int).SetString(c.Value, 10)
	if !ok {
		return checkResult{}, fmt.Errorf("bad value %q", c.Value)
	}
	ch := check.Check{Nonce: mustHex(c.Nonce), ChainID: types.ChainID(c.ChainID), DueBlock: uint64(c.DueBlock), Coin: types.CoinID(c.Coin), Value: value, GasCoin: types.CoinID(c.GasCoin)}
	passKey := passwordKey(c.Password)
	hashWithoutLock := ch.HashWithoutLock()
	lock, err := crypto.Sign(hashWithoutLock[:], passKey)
	if err != nil {
		return checkResult{}, err
	}
	ch.Lock = new(big.Int).SetBytes(lock)
	if err := ch.Sign(key(c.Key)); err != nil {
		return checkResult{}, err
	}
	raw, err := rlp.EncodeToBytes(&ch)
	if err != nil {
		return checkResult{}, err
	}
	redeemer := types.HexToAddress(c.Redeemer)
	proof, err := crypto.Sign(proofHash(redeemer), passKey)
	if err != nil {
		return checkResult{}, err
	}
	// Verified the way redeem_check.go does: issuer from the signature, lock key against the proof key.
	decoded, err := check.DecodeFromBytes(raw)
	if err != nil {
		return checkResult{}, err
	}
	issuer, err := decoded.Sender()
	if err != nil {
		return checkResult{}, err
	}
	lockPub, err := decoded.LockPubKey()
	if err != nil {
		return checkResult{}, err
	}
	proofPub, err := crypto.Ecrecover(proofHash(redeemer), proof)
	if err != nil {
		return checkResult{}, err
	}
	wrong, err := crypto.Sign(proofHash(redeemer), passwordKey(c.Password+"x"))
	if err != nil {
		return checkResult{}, err
	}
	wrongPub, _ := crypto.Ecrecover(proofHash(redeemer), wrong)
	return checkResult{
		Name: c.Name, Check: "Mc" + hex.EncodeToString(raw), Proof: hex.EncodeToString(proof), Issuer: issuer.String(),
		ProofOK: bytes.Equal(lockPub, proofPub), WrongPass: bytes.Equal(lockPub, wrongPub),
	}, nil
}

func proofHash(redeemer types.Address) []byte {
	encoded, err := rlp.EncodeToBytes([]interface{}{redeemer})
	if err != nil {
		panic(err)
	}
	return crypto.Keccak256(encoded)
}

func passwordKey(password string) *ecdsa.PrivateKey {
	h := sha256.Sum256([]byte(password))
	k, err := crypto.ToECDSA(h[:])
	if err != nil {
		panic(err)
	}
	return k
}

func key(hexKey string) *ecdsa.PrivateKey {
	k, err := crypto.ToECDSA(mustHex(hexKey))
	if err != nil {
		fail("key: %v", err)
	}
	return k
}

var (
	addressType = reflect.TypeOf(types.Address{})
	pubkeyType  = reflect.TypeOf(types.Pubkey{})
	symbolType  = reflect.TypeOf(types.CoinSymbol{})
	bigType     = reflect.TypeOf((*big.Int)(nil))
)

func fill(v reflect.Value, raw json.RawMessage) error {
	if len(raw) == 0 {
		return nil
	}
	switch v.Type() {
	case addressType:
		var s string
		if err := json.Unmarshal(raw, &s); err != nil || !strings.HasPrefix(s, "Mx") || len(s) != 42 {
			return fmt.Errorf("address %s", raw)
		}
		v.Set(reflect.ValueOf(types.HexToAddress(s)))
		return nil
	case pubkeyType:
		var s string
		if err := json.Unmarshal(raw, &s); err != nil || !strings.HasPrefix(s, "Mp") || len(s) != 66 {
			return fmt.Errorf("pubkey %s", raw)
		}
		var p types.Pubkey
		copy(p[:], mustHex(s[2:]))
		v.Set(reflect.ValueOf(p))
		return nil
	case symbolType:
		var s string
		if err := json.Unmarshal(raw, &s); err != nil {
			return err
		}
		v.Set(reflect.ValueOf(types.StrToCoinSymbol(s)))
		return nil
	case bigType:
		var s string
		if err := json.Unmarshal(raw, &s); err != nil {
			return fmt.Errorf("big.Int must be a decimal string: %s", raw)
		}
		b, ok := new(big.Int).SetString(s, 10)
		if !ok {
			return fmt.Errorf("bad big.Int %q", s)
		}
		v.Set(reflect.ValueOf(b))
		return nil
	}
	switch v.Kind() {
	case reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		n, err := parseUint(raw)
		if err != nil {
			return err
		}
		if v.OverflowUint(n) {
			return fmt.Errorf("%d overflows %s", n, v.Type())
		}
		v.SetUint(n)
	case reflect.Bool:
		var b bool
		if err := json.Unmarshal(raw, &b); err != nil {
			return err
		}
		v.SetBool(b)
	case reflect.String:
		var s string
		if err := json.Unmarshal(raw, &s); err != nil {
			return err
		}
		v.SetString(s)
	case reflect.Array:
		if v.Type().Elem().Kind() != reflect.Uint8 {
			return fmt.Errorf("unsupported array %s", v.Type())
		}
		var s string
		if err := json.Unmarshal(raw, &s); err != nil {
			return err
		}
		b := mustHex(s)
		if len(b) != v.Len() {
			return fmt.Errorf("%s needs %d bytes", v.Type(), v.Len())
		}
		reflect.Copy(v, reflect.ValueOf(b))
	case reflect.Slice:
		if v.Type().Elem().Kind() == reflect.Uint8 {
			var s string
			if err := json.Unmarshal(raw, &s); err != nil {
				return err
			}
			v.SetBytes(mustHex(s))
			return nil
		}
		var items []json.RawMessage
		if err := json.Unmarshal(raw, &items); err != nil {
			return err
		}
		slice := reflect.MakeSlice(v.Type(), len(items), len(items))
		for i, item := range items {
			if err := fill(slice.Index(i), item); err != nil {
				return err
			}
		}
		v.Set(slice)
	case reflect.Struct:
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(raw, &fields); err != nil {
			return err
		}
		for name, value := range fields {
			f := v.FieldByName(name)
			if !f.IsValid() || !f.CanSet() {
				return fmt.Errorf("%s has no field %s", v.Type(), name)
			}
			if err := fill(f, value); err != nil {
				return fmt.Errorf("%s.%s: %v", v.Type(), name, err)
			}
		}
	default:
		return fmt.Errorf("unsupported kind %s", v.Kind())
	}
	return nil
}

func mustHex(s string) []byte {
	b, err := hex.DecodeString(strings.TrimPrefix(s, "0x"))
	if err != nil {
		fail("hex %q: %v", s, err)
	}
	return b
}

func fail(format string, args ...interface{}) {
	fmt.Fprintf(os.Stderr, "oracle: "+format+"\n", args...)
	os.Exit(1)
}
