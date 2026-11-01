// Writes the shared test cases (tests/Minter.Protocol.Checks/Fixtures/cases.json): every V3 transaction type with
// edge values, multisignatures, checks and multisig addresses. Keys are public TEST keys: sha256("minterwallet-test-N").
import { writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const sha = s => createHash('sha256').update(s).digest('hex');
const key = n => sha(`minterwallet-test-${n}`);
const addr = n => 'Mx' + sha(`minterwallet-address-${n}`).slice(0, 40);
const pub = n => 'Mp' + sha(`minterwallet-validator-${n}`);
const MAX256 = (2n ** 256n - 1n).toString();
const COIN = '1000000000000000000';
const U32 = 4294967295, U64 = '18446744073709551615';

const priceFields = ['PayloadByte', 'Send', 'BuyBancor', 'SellBancor', 'SellAllBancor', 'BuyPoolBase', 'BuyPoolDelta', 'SellPoolBase',
  'SellPoolDelta', 'SellAllPoolBase', 'SellAllPoolDelta', 'CreateTicker3', 'CreateTicker4', 'CreateTicker5', 'CreateTicker6',
  'CreateTicker7to10', 'CreateCoin', 'CreateToken', 'RecreateCoin', 'RecreateToken', 'DeclareCandidacy', 'Delegate', 'Unbond',
  'RedeemCheck', 'SetCandidateOn', 'SetCandidateOff', 'CreateMultisig', 'MultisendBase', 'MultisendDelta', 'EditCandidate',
  'SetHaltBlock', 'EditTickerOwner', 'EditMultisig', 'EditCandidatePublicKey', 'CreateSwapPool', 'AddLiquidity', 'RemoveLiquidity',
  'EditCandidateCommission', 'MintToken', 'BurnToken', 'VoteCommission', 'VoteUpdate', 'FailedTx', 'AddLimitOrder',
  'RemoveLimitOrder', 'MoveStake', 'LockStake', 'Lock'];
// Distinct values: a price read from the wrong field shows up as a wrong commission.
const prices = { Coin: 1993 };
priceFields.forEach((f, i) => prices[f] = (1000000000000000n * BigInt(i + 1) + BigInt(i)).toString());

const base = { key: key(1), nonce: 1, chainId: 1, gasPrice: 1, gasCoin: 0, payload: '', serviceData: '' };
const tx = (name, type, data, extra = {}) => ({ ...base, name, type, data, ...extra });
const hex = s => Buffer.from(s, 'utf8').toString('hex');
const repeat = (byte, n) => byte.repeat(n);

const transactions = [
  tx('send-min', 0x01, { Coin: 0, To: addr(1), Value: '1' }),
  tx('send-zero', 0x01, { Coin: 0, To: addr(1), Value: '0' }),
  tx('send-max-fields', 0x01, { Coin: U32, To: addr(2), Value: MAX256 },
    { nonce: U64, chainId: 2, gasPrice: U32, gasCoin: U32, payload: hex('привет, Minter ✓'), serviceData: repeat('ab', 128), key: key(2) }),
  tx('send-payload-10000', 0x01, { Coin: 1, To: addr(3), Value: COIN }, { payload: repeat('7a', 10000) }),
  tx('send-small-nonces', 0x01, { Coin: 127, To: addr(1), Value: '127' }, { nonce: 127, gasPrice: 128, gasCoin: 128 }),
  tx('sell-coin', 0x02, { CoinToSell: 0, ValueToSell: COIN, CoinToBuy: 1902, MinimumValueToBuy: '0' }),
  tx('sell-all-coin', 0x03, { CoinToSell: 1902, CoinToBuy: 0, MinimumValueToBuy: '12345678901234567890' }),
  tx('buy-coin', 0x04, { CoinToBuy: 1902, ValueToBuy: COIN, CoinToSell: 0, MaximumValueToSell: MAX256 }),
  tx('create-coin', 0x05, { Name: 'Тестовая монета ✓', Symbol: 'TESTCOIN10', InitialAmount: '100' + COIN.slice(1), InitialReserve: '20000' + COIN.slice(1), ConstantReserveRatio: 80, MaxSupply: '1000000000000000' + COIN.slice(1) }),
  tx('create-coin-3', 0x05, { Name: '', Symbol: 'ABC', InitialAmount: COIN, InitialReserve: COIN, ConstantReserveRatio: 10, MaxSupply: COIN }),
  tx('create-coin-4', 0x05, { Name: 'x', Symbol: 'ABCD', InitialAmount: COIN, InitialReserve: COIN, ConstantReserveRatio: 100, MaxSupply: COIN }),
  tx('create-coin-6', 0x05, { Name: 'x', Symbol: 'ABCDEF', InitialAmount: COIN, InitialReserve: COIN, ConstantReserveRatio: 50, MaxSupply: COIN }),
  tx('declare-candidacy', 0x06, { Address: addr(4), PubKey: pub(1), Commission: 10, Coin: 0, Stake: '1000' + COIN.slice(1) }),
  tx('delegate', 0x07, { PubKey: pub(1), Coin: 0, Value: COIN }),
  tx('unbond', 0x08, { PubKey: pub(2), Coin: 1902, Value: '1' }),
  tx('redeem-check', 0x09, { RawCheck: repeat('c3', 180), Proof: repeat('5a', 64) + '01' }),
  tx('set-candidate-online', 0x0a, { PubKey: pub(1) }),
  tx('set-candidate-offline', 0x0b, { PubKey: pub(2) }),
  tx('create-multisig', 0x0c, { Threshold: 3, Weights: [1, 2, 1023], Addresses: [addr(1), addr(2), addr(3)] }),
  tx('multisend-1', 0x0d, { List: [{ Coin: 0, To: addr(1), Value: COIN }] }),
  tx('multisend-100', 0x0d, { List: Array.from({ length: 100 }, (_, i) => ({ Coin: i, To: addr(i + 10), Value: (BigInt(i) * 10n ** 17n).toString() })) }),
  tx('edit-candidate', 0x0e, { PubKey: pub(1), RewardAddress: addr(1), OwnerAddress: addr(2), ControlAddress: addr(3) }),
  tx('set-halt-block', 0x0f, { PubKey: pub(1), Height: U64 }),
  tx('recreate-coin', 0x10, { Name: 'Recreated', Symbol: 'TESTCOIN', InitialAmount: COIN, InitialReserve: '10000' + COIN.slice(1), ConstantReserveRatio: 70, MaxSupply: MAX256 }),
  tx('edit-coin-owner', 0x11, { Symbol: 'TESTCOIN', NewOwner: addr(5) }),
  tx('edit-multisig', 0x12, { Threshold: 1, Weights: [1], Addresses: [addr(6)] }),
  tx('edit-candidate-public-key', 0x14, { PubKey: pub(1), NewPubKey: pub(2) }),
  tx('add-liquidity', 0x15, { Coin0: 0, Coin1: 1993, Volume0: COIN, MaximumVolume1: MAX256 }),
  tx('remove-liquidity', 0x16, { Coin0: 0, Coin1: 1993, Liquidity: '123456789', MinimumVolume0: '1', MinimumVolume1: '0' }),
  tx('sell-swap-pool-2', 0x17, { Coins: [0, 1993], ValueToSell: COIN, MinimumValueToBuy: '1' }),
  tx('sell-swap-pool-5', 0x17, { Coins: [0, 1993, 1902, 1087, 0], ValueToSell: '18925820000000000000000', MinimumValueToBuy: '18925830000000000000000' }),
  tx('buy-swap-pool-3', 0x18, { Coins: [1993, 0, 1902], ValueToBuy: COIN, MaximumValueToSell: MAX256 }),
  tx('sell-all-swap-pool-4', 0x19, { Coins: [1902, 1087, 907, 0], MinimumValueToBuy: '0' }),
  tx('edit-candidate-commission', 0x1a, { PubKey: pub(1), Commission: 100 }),
  tx('move-stake', 0x1b, { FromPubKey: pub(1), ToPubKey: pub(2), Coin: 0, Value: COIN }),
  tx('mint-token', 0x1c, { Coin: 2000, Value: COIN }),
  tx('burn-token', 0x1d, { Coin: 2000, Value: '1' }),
  tx('create-token', 0x1e, { Name: 'Token', Symbol: 'TOKEN', InitialAmount: COIN, MaxSupply: '10' + COIN.slice(1), Mintable: true, Burnable: false }),
  tx('create-token-7', 0x1e, { Name: 'Token7', Symbol: 'TOKEN77', InitialAmount: COIN, MaxSupply: COIN, Mintable: false, Burnable: true }),
  tx('recreate-token', 0x1f, { Name: 'Token2', Symbol: 'TOKEN', InitialAmount: '0', MaxSupply: COIN, Mintable: true, Burnable: true }),
  tx('vote-commission', 0x20, { PubKey: pub(1), Height: 40400000, Coin: 1993, ...Object.fromEntries(priceFields.map((f, i) => [f, (BigInt(i) * 7n).toString()])) }),
  tx('vote-commission-more', 0x20, { PubKey: pub(2), Height: 1, Coin: 0, ...Object.fromEntries(priceFields.map(f => [f, COIN])), More: ['7', '0', MAX256] }),
  tx('vote-update', 0x21, { Version: 'v3.4.0', PubKey: pub(1), Height: 40500000 }),
  tx('create-swap-pool', 0x22, { Coin0: 1902, Coin1: 2000, Volume0: COIN, Volume1: '2' + COIN.slice(1) }),
  tx('add-limit-order', 0x23, { CoinToSell: 0, ValueToSell: COIN, CoinToBuy: 1993, ValueToBuy: '5' + COIN.slice(1) }),
  tx('remove-limit-order', 0x24, { ID: U32 }),
  tx('lock-stake', 0x25, {}),
  tx('lock', 0x26, { DueBlock: U32, Coin: 0, Value: COIN }),
  tx('multisig-send', 0x01, { Coin: 0, To: addr(1), Value: COIN }, { multisig: { address: addr(7), keys: [key(2), key(3)] } }),
  tx('multisig-sell-swap-pool', 0x17, { Coins: [0, 1993], ValueToSell: COIN, MinimumValueToBuy: '1' }, { nonce: 5, multisig: { address: addr(8), keys: [key(3), key(4), key(2)] } }),
];

const checks = [
  { name: 'check-basic', key: key(1), password: 'pass', nonce: hex('1'), chainId: 1, dueBlock: 999999999, coin: 0, value: COIN, gasCoin: 0, redeemer: addr(1) },
  { name: 'check-unicode', key: key(2), password: 'пароль ✓ 123', nonce: repeat('ff', 16), chainId: 2, dueBlock: U64, coin: 1902, value: MAX256, gasCoin: 1993, redeemer: addr(2) },
  { name: 'check-empty-password', key: key(3), password: '', nonce: '', chainId: 1, dueBlock: 1, coin: U32, value: '0', gasCoin: U32, redeemer: addr(3) },
];

// Integers above 2^53 are written as decimal strings; both readers accept numbers and strings.
const multisigAddresses = [0, 1, 255, 256, U64].map(nonce => ({ owner: addr(1), nonce }));

const target = new URL('../../tests/Minter.Protocol.Checks/Fixtures/cases.json', import.meta.url);
writeFileSync(target, JSON.stringify({ prices, transactions, checks, multisigAddresses }, null, 2) + '\n');
console.log(`cases: ${transactions.length} transactions (${new Set(transactions.map(t => t.type)).size} types), ${checks.length} checks, ${multisigAddresses.length} multisig addresses`);
