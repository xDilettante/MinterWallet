// Devnet: a one-validator Minter network for end-to-end tests without real funds. Writes genesis.json,
// priv_validator.json and config.toml for upstream minter-go-node v3.3.0 (built for Linux, run in WSL with
// --testnet, so transactions carry chain ID 2 and are invalid on mainnet). The test accounts are the public TEST keys
// sha256("minterwallet-test-N") of tests/Minter.Protocol.Checks. The node is isolated: no seeds, no peers, no PEX.
package main

import (
	"crypto/sha256"
	"encoding/json"
	"flag"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"time"

	"github.com/MinterTeam/minter-go-node/config"
	"github.com/MinterTeam/minter-go-node/coreV2/state/candidates"
	"github.com/MinterTeam/minter-go-node/coreV2/types"
	"github.com/MinterTeam/minter-go-node/crypto"
	"github.com/MinterTeam/minter-go-node/helpers"
	tmjson "github.com/tendermint/tendermint/libs/json"
	"github.com/tendermint/tendermint/privval"
	tmtypes "github.com/tendermint/tendermint/types"
)

func main() {
	home := flag.String("home", "", "node home directory to create")
	accounts := flag.Int("accounts", 4, "number of funded test accounts")
	flag.Parse()
	if *home == "" {
		fail("-home is required")
	}
	if _, err := os.Stat(filepath.Join(*home, "config", "genesis.json")); err == nil {
		fail("%s already has a genesis; remove the directory to start a new network", *home)
	}
	must(os.MkdirAll(filepath.Join(*home, "config"), 0o700))
	must(os.MkdirAll(filepath.Join(*home, "data"), 0o700))

	// Minter's config keeps the validator key in config/priv_validator.json (not Tendermint's priv_validator_key.json).
	pv := privval.GenFilePV(filepath.Join(*home, "config", "priv_validator.json"), filepath.Join(*home, "config", "priv_validator_state.json"))
	pv.Save()
	var pubkey types.Pubkey
	copy(pubkey[:], pv.Key.PubKey.Bytes())

	var funded []types.Account
	var addresses []string
	for i := 1; i <= *accounts; i++ {
		h := sha256.Sum256([]byte(fmt.Sprintf("minterwallet-test-%d", i)))
		key, err := crypto.ToECDSA(h[:])
		must(err)
		address := crypto.PubkeyToAddress(key.PublicKey)
		addresses = append(addresses, address.String())
		funded = append(funded, types.Account{Address: address, Balance: []types.Balance{{Coin: uint64(types.GetBaseCoinID()), Value: helpers.BipToPip(big.NewInt(1_000_000_000)).String()}}})
	}

	// The first test account also has a genesis stake: a fresh delegation becomes a stake only at the next stake update
	// (every 120 blocks with --testnet), so Unbond can be tested at once only against a stake that already exists.
	// The bulk of the validator's stake belongs to the second test account, not to the DAO or developers: v330 runs
	// FixStakesAfter10509400 once, and it would look such stakes up in a mainnet table.
	ownStake := helpers.BipToPip(big.NewInt(1_000))
	stake := new(big.Int).Add(helpers.BipToPip(big.NewInt(1_000_000)), ownStake).String()
	owner := funded[0].Address
	appState := types.AppState{
		// The current mainnet version, so the gate's executor (ported for heights after v330) runs the devnet.
		Version:      "v330",
		TotalSlashed: "0",
		Accounts:     funded,
		Validators: []types.Validator{{
			TotalBipStake: stake, PubKey: pubkey, AccumReward: "0", AbsentTimes: types.NewBitArray(24),
		}},
		Candidates: []types.Candidate{{
			ID: 1, RewardAddress: owner, OwnerAddress: owner, ControlAddress: owner, TotalBipStake: stake, PubKey: pubkey, Commission: 10,
			Stakes: []types.Stake{
				{Owner: funded[1].Address, Coin: uint64(types.GetBaseCoinID()), Value: helpers.BipToPip(big.NewInt(1_000_000)).String(), BipValue: helpers.BipToPip(big.NewInt(1_000_000)).String()},
				{Owner: owner, Coin: uint64(types.GetBaseCoinID()), Value: ownStake.String(), BipValue: ownStake.String()},
			},
			Status: candidates.CandidateStatusOnline,
		}},
		Commission: commission(),
		// Read unconditionally by State.Import and InitChain: block gas limit, emission and the reward price.
		MaxGas:   100000,
		Emission: helpers.BipToPip(big.NewInt(int64(*accounts)*1_000_000_000 + 1_001_000)).String(),
		PrevReward: types.RewardPrice{
			Time: uint64(time.Now().UnixNano()), AmountBIP: helpers.BipToPip(big.NewInt(1)).String(),
			AmountUSDT: helpers.BipToPip(big.NewInt(1)).String(), Reward: helpers.BipToPip(big.NewInt(100)).String(),
		},
	}
	appStateJSON, err := tmjson.Marshal(appState)
	must(err)
	// Initial height 120 (a stake period): v330 starts there and its one-time stake fix window (120, 240) holds no stake
	// update, so every block from the first snapshot on (200) runs the same code as mainnet today.
	genesis := tmtypes.GenesisDoc{ChainID: "minter-devnet", InitialHeight: 120, GenesisTime: time.Now(), AppState: json.RawMessage(appStateJSON)}
	must(genesis.ValidateAndComplete())
	must(genesis.SaveAs(filepath.Join(*home, "config", "genesis.json")))

	cfg := config.DefaultConfig()
	cfg.SetRoot(*home)
	cfg.Moniker = "minterwallet-devnet"
	cfg.P2P.Seeds = ""
	cfg.P2P.PersistentPeers = ""
	cfg.P2P.PexReactor = false
	// P2P and RPC listen on every interface of the WSL VM, so a gate on Windows can follow the node (its address is the
	// WSL IP); state sync snapshots every 100 blocks give the gate its starting state.
	cfg.P2P.ListenAddress = "tcp://0.0.0.0:36656"
	cfg.RPC.ListenAddress = "tcp://0.0.0.0:36657"
	cfg.SnapshotInterval = 100
	cfg.BaseConfig.APIv2ListenAddress = "tcp://0.0.0.0:38843"
	cfg.BaseConfig.GRPCListenAddress = "tcp://127.0.0.1:38842"
	config.WriteConfigFile(filepath.Join(*home, "config", "config.toml"), cfg)

	out, _ := json.MarshalIndent(map[string]interface{}{"home": *home, "chain_id": genesis.ChainID, "validator": pubkey.String(), "accounts": addresses,
		"api": "http://127.0.0.1:38843/v2/", "run": "minter-linux node --home-dir=" + *home + " --testnet"}, "", "  ")
	fmt.Println(string(out))
}

// Mainnet-like prices in BIP (coin 0), so the wallet's fee display can be checked against real magnitudes.
func commission() types.Commission {
	return types.Commission{
		Coin: 0, PayloadByte: "2000000000000000", Send: "10000000000000000",
		BuyBancor: "100000000000000000", SellBancor: "100000000000000000", SellAllBancor: "100000000000000000",
		BuyPoolBase: "100000000000000000", BuyPoolDelta: "50000000000000000", SellPoolBase: "100000000000000000", SellPoolDelta: "50000000000000000",
		SellAllPoolBase: "100000000000000000", SellAllPoolDelta: "50000000000000000",
		CreateTicker3: "1000000000000000000000000", CreateTicker4: "100000000000000000000000", CreateTicker5: "10000000000000000000000",
		CreateTicker6: "1000000000000000000000", CreateTicker7_10: "100000000000000000000",
		CreateCoin: "0", CreateToken: "0", RecreateCoin: "10000000000000000000000", RecreateToken: "10000000000000000000000",
		DeclareCandidacy: "10000000000000000000", Delegate: "200000000000000000", Unbond: "200000000000000000", RedeemCheck: "30000000000000000",
		SetCandidateOn: "100000000000000000", SetCandidateOff: "100000000000000000", CreateMultisig: "100000000000000000",
		MultisendBase: "10000000000000000", MultisendDelta: "5000000000000000", EditCandidate: "10000000000000000000",
		SetHaltBlock: "1000000000000000000", EditTickerOwner: "10000000000000000000000", EditMultisig: "1000000000000000000",
		EditCandidatePublicKey: "100000000000000000000000", CreateSwapPool: "1000000000000000000", AddLiquidity: "100000000000000000",
		RemoveLiquidity: "100000000000000000", EditCandidateCommission: "10000000000000000000", MintToken: "100000000000000000",
		BurnToken: "100000000000000000", VoteCommission: "1000000000000000000", VoteUpdate: "1000000000000000000",
		FailedTx: "10000000000000000", AddLimitOrder: "100000000000000000", RemoveLimitOrder: "100000000000000000",
		MoveStake: "200000000000000000", LockStake: "200000000000000000", Lock: "100000000000000000",
	}
}

func must(err error) {
	if err != nil {
		fail("%v", err)
	}
}

func fail(format string, args ...interface{}) {
	fmt.Fprintf(os.Stderr, "devnet: "+format+"\n", args...)
	os.Exit(1)
}
